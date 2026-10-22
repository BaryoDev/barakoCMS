using barakoCMS.Infrastructure.Auth;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation.Results;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// The bundle types are obsolete for callers outside the module, and this is the module.
#pragma warning disable CS0618

namespace BarakoCMS.Portability;

/// <summary>
/// POST /api/portability/import. Upserts content types (by name) then recreates content via events.
/// Pass <c>dryRun: true</c> to preview the counts without writing.
/// </summary>
/// <remarks>
/// Every type goes through the checks <c>POST /api/content-types</c> and add field run, and every
/// record through the same write path as <c>POST /api/contents</c>: write-side sensitivity keyed on
/// the caller, schema validation, lifecycle hooks and the type's initial state.
///
/// All or nothing. Any refused type or record answers 400 naming each one, as
/// <c>contentTypes[i]</c> or <c>contents[i]</c>, and nothing is written. Partial success was the
/// other choice and it is the worse one here: import always creates new entries, so rerunning a
/// half-applied bundle after fixing it would duplicate everything that landed the first time. A dry
/// run refuses exactly what the real run would.
/// </remarks>
[Obsolete("Use the config engine instead: GET /api/config/export, and POST /api/config/plan and /api/config/apply, which accept a Portability bundle as a document. Removal planned for barakoCMS 6.0.")]
public class ImportEndpoint : Endpoint<ImportRequest, ImportReport>
{
    private readonly IDocumentSession _session;
    private readonly IContentWriter _contentWriter;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    /// <remarks>
    /// One constructor, and it stays that way. The sourcing policy below is resolved rather than
    /// injected for two reasons: this constructor is public API under CLAUDE.md section 6, and
    /// FastEndpoints refuses to build an endpoint that offers it a choice of constructors, so adding
    /// an overload the way section 6 asks throws at startup instead of compiling.
    /// </remarks>
    public ImportEndpoint(IDocumentSession session, barakoCMS.Infrastructure.Multitenancy.TenantContext tenant, IContentWriter contentWriter)
    {
        _contentWriter = contentWriter;
        _session = session;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Post("/api/portability/import");
        Definition.RequireCapability(
            PortabilityCapabilities.ImportContent, PortabilityCapabilities.Defaults.LegacyRoles);
        Claims("UserId");
    }

    public override async Task HandleAsync(ImportRequest req, CancellationToken ct)
    {
        Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId);
        var report = new ImportReport { DryRun = req.DryRun };
        var configuration = Resolve<IConfiguration>();
        // A null entry in either list is dropped here rather than failing later on a null
        // dereference as a 500.
        var bundleTypes = (req.ContentTypes ?? []).Where(t => t is not null).ToList();
        var records = (req.Contents ?? []).Where(r => r is not null).ToList();

        CheckLimits(bundleTypes.Count, records.Count, configuration);

        var existing = (await _session.Query<ContentTypeDefinition>().ToListAsync(ct)).ToList();
        await CheckTypesAsync(bundleTypes, existing, configuration, ct);

        var (toStore, created) = Upsert(bundleTypes, existing, report);

        // Each record is checked against its type as this bundle leaves it, not as it is stored.
        // No singleton cap: an import is a restore and lands what the bundle holds.
        var batch = new ContentCreateBatch { CapSingletons = false };
        foreach (var definition in existing)
        {
            batch.UseSchema(definition);
        }

        var prepared = BundleEntries.Prepare(records, existing, batch, report);
        foreach (var error in prepared.Errors)
        {
            AddError(error);
        }

        ThrowIfAnyErrors();

        // One transaction, each write visible to the next: a lifecycle hook numbering journal
        // entries or walking a page's ancestors sees the entries written before it in this bundle.
        // Rolled back on any refusal and on a dry run, so a dry run runs every check and keeps
        // nothing.
        var failures = await Resolve<IContentBatchRunner>().RunAsync(async (scope, token) =>
        {
            var session = scope.GetRequiredService<IDocumentSession>();

            foreach (var definition in toStore)
            {
                session.Store(definition);
            }

            // Recorded here as well as in the create endpoint, because this is the other way a
            // content type comes into existence. A type with no policy row reads as not event
            // sourced, which is the right answer, but nothing stops the name being claimed as event
            // sourced later. DecideAsync never overwrites, so a name that already has a decision
            // keeps it.
            var sourcing = scope.GetRequiredService<IContentSourcingPolicy>();
            foreach (var definition in created)
            {
                await sourcing.DecideAsync(definition.Name, false, token);
            }

            await session.SaveChangesAsync(token);

            var refused = await BundleEntries.WriteAsync(
                session, scope.GetRequiredService<IContentCreator>(), prepared, userId, HttpContext, batch, token);

            var commit = refused.Count == 0 && !req.DryRun;
            if (commit)
            {
                await AuditLog.RecordAsync(session, _tenant.Slug, "portability.imported", userId, User.FindFirst("Username")?.Value,
                    metadata: new()
                    {
                        ["contentTypesCreated"] = report.ContentTypesCreated,
                        ["contentTypesUpdated"] = report.ContentTypesUpdated,
                        ["contentsCreated"] = report.ContentsCreated,
                    }, ct: token);
                await session.SaveChangesAsync(token);
            }

            return new ContentBatchOutcome<List<ValidationFailure>>(refused, commit);
        }, ct);

        foreach (var failure in failures.OrderBy(f => f.PropertyName, StringComparer.Ordinal))
        {
            AddError(failure);
        }

        ThrowIfAnyErrors();

        await Send.ResponseAsync(report, cancellation: ct);
    }

    /// <summary>
    /// Bounded before anything is read, since every record costs a validation pass and all of them
    /// are held in one unit of work.
    /// </summary>
    private void CheckLimits(int types, int records, IConfiguration configuration)
    {
        var maxRecords = PortabilityLimits.MaxImportRecords(configuration);
        if (records > maxRecords)
        {
            AddError($"A bundle may hold at most {maxRecords} records and this one holds {records}. "
                     + "Export fewer types per bundle, or raise Portability:MaxImportRecords.");
            ThrowIfAnyErrors();
        }

        if (types > PortabilityLimits.MaxImportContentTypes)
        {
            AddError($"A bundle may hold at most {PortabilityLimits.MaxImportContentTypes} content types "
                     + $"and this one holds {types}.");
            ThrowIfAnyErrors();
        }
    }

    /// <summary>
    /// Refuses the whole bundle before anything is stored when one type would not be written by the
    /// content-type endpoints, so a refused import leaves no type and no content behind.
    /// </summary>
    private async Task CheckTypesAsync(
        List<ContentTypeDefinition> bundleTypes, List<ContentTypeDefinition> existing, IConfiguration configuration, CancellationToken ct)
    {
        // Same rule as the content-type endpoints: a type already stored over the cap may be
        // imported again at its size, only growth past the cap is refused.
        var maxFields = ContentTypeFieldLimit.Resolve(configuration);
        foreach (var type in bundleTypes.Where(t => !string.IsNullOrWhiteSpace(t.Name)))
        {
            if (ContentTypeUpsert.FieldCapError(type, ContentTypeUpsert.StoredMatch(existing, type.Name), maxFields) is { } tooMany)
            {
                AddError($"Content type '{Shorten(type.Name)}' was not imported. {tooMany}");
                ThrowIfAnyErrors();
            }
        }

        for (var i = 0; i < bundleTypes.Count; i++)
        {
            var type = bundleTypes[i];
            if (string.IsNullOrWhiteSpace(type.Name))
            {
                continue;
            }

            var match = ContentTypeUpsert.StoredMatch(existing, type.Name);
            await ContentTypeUpsert.PrepareAsync(_session, type, match, ct);

            foreach (var error in await ContentTypeUpsert.ErrorsAsync(
                         Resolve<IContentTypeValidatorService>(), Resolve<IContentSourcingPolicy>(), _session,
                         Resolve<barakoCMS.Infrastructure.Realtime.TopicRegistry>(), type, match, maxFields, ContentTypeUpsert.Wording.Import, ct))
            {
                AddError(new ValidationFailure($"contentTypes[{i}]", $"Content type '{Shorten(type.Name)}': {error}"));
            }
        }

        // Records are checked against the types as this bundle leaves them, so there is no point
        // checking them against a type that is itself refused.
        ThrowIfAnyErrors();
    }

    /// <summary>
    /// The definitions to store: each stored type with the bundle's written over it, and each new
    /// one. A new type joins <paramref name="existing"/> whether or not this is a dry run, so records
    /// later in the same bundle resolve against a type this bundle creates.
    /// </summary>
    private static (List<ContentTypeDefinition> ToStore, List<ContentTypeDefinition> Created) Upsert(
        List<ContentTypeDefinition> bundleTypes, List<ContentTypeDefinition> existing, ImportReport report)
    {
        var toStore = new List<ContentTypeDefinition>();
        var created = new List<ContentTypeDefinition>();

        foreach (var type in bundleTypes.Where(t => !string.IsNullOrWhiteSpace(t.Name)))
        {
            if (ContentTypeUpsert.StoredMatch(existing, type.Name) is { } match)
            {
                report.ContentTypesUpdated++;
                ContentTypeUpsert.MergeInto(match, type);
                toStore.Add(match);
                continue;
            }

            report.ContentTypesCreated++;
            var definition = ContentTypeUpsert.Create(type);
            existing.Add(definition);
            toStore.Add(definition);
            created.Add(definition);
        }

        return (toStore, created);
    }

    private static string Shorten(string name) => name.Length > 100 ? name[..100] + "..." : name;
}
