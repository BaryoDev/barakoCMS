using barakoCMS.Infrastructure.Auth;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using FluentValidation.Results;
using Marten;
using Microsoft.Extensions.Configuration;

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
            PortabilityCapabilities.ImportContent, PortabilityCapabilities.LegacyRoles);
        Claims("UserId");
    }

    public override async Task HandleAsync(ImportRequest req, CancellationToken ct)
    {
        Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId);
        var report = new ImportReport { DryRun = req.DryRun };
        var configuration = Resolve<IConfiguration>();
        var bundleTypes = req.ContentTypes ?? [];
        var records = req.Contents ?? [];

        // Bounded before anything is read, since every record costs a validation pass and all of
        // them are held in one unit of work.
        var maxRecords = PortabilityLimits.MaxImportRecords(configuration);
        if (records.Count > maxRecords)
        {
            AddError($"A bundle may hold at most {maxRecords} records and this one holds {records.Count}. "
                     + "Export fewer types per bundle, or raise Portability:MaxImportRecords.");
            ThrowIfAnyErrors();
        }

        if (bundleTypes.Count > PortabilityLimits.MaxImportContentTypes)
        {
            AddError($"A bundle may hold at most {PortabilityLimits.MaxImportContentTypes} content types "
                     + $"and this one holds {bundleTypes.Count}.");
            ThrowIfAnyErrors();
        }

        var existing = (await _session.Query<ContentTypeDefinition>().ToListAsync(ct)).ToList();

        // Checked for the whole bundle before anything is stored, so a refused import leaves no
        // type and no content behind. Same rule as the content-type endpoints: a type already
        // stored over the cap may be imported again at its size, only growth past the cap is refused.
        var maxFields = barakoCMS.Infrastructure.Services.ContentTypeFieldLimit.Resolve(configuration);
        foreach (var type in bundleTypes)
        {
            if (string.IsNullOrWhiteSpace(type.Name)) continue;

            var incoming = type.Fields?.Count ?? 0;
            var stored = existing.FirstOrDefault(t =>
                t.Name.Equals(type.Name, StringComparison.OrdinalIgnoreCase))?.Fields?.Count ?? 0;

            if (incoming > maxFields && incoming > stored)
            {
                var name = type.Name.Length > 100 ? type.Name[..100] + "..." : type.Name;
                AddError($"Content type '{name}' was not imported. "
                         + barakoCMS.Infrastructure.Services.ContentTypeFieldLimit.TooMany(maxFields, incoming));
                ThrowIfAnyErrors();
            }
        }

        for (var i = 0; i < bundleTypes.Count; i++)
        {
            var type = bundleTypes[i];
            if (string.IsNullOrWhiteSpace(type.Name)) continue;

            type.Fields ??= [];
            var match = existing.FirstOrDefault(t =>
                t.Name.Equals(type.Name, StringComparison.OrdinalIgnoreCase));

            foreach (var error in await TypeErrorsAsync(type, match, maxFields, ct))
                AddError(new ValidationFailure($"contentTypes[{i}]", $"Content type '{Shorten(type.Name)}': {error}"));
        }

        // Records are checked against the types as this bundle leaves them, so there is no point
        // checking them against a type that is itself refused.
        ThrowIfAnyErrors();

        var toStore = new List<ContentTypeDefinition>();
        var created = new List<ContentTypeDefinition>();

        foreach (var type in bundleTypes)
        {
            if (string.IsNullOrWhiteSpace(type.Name)) continue;

            var match = existing.FirstOrDefault(t =>
                t.Name.Equals(type.Name, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                report.ContentTypesUpdated++;

                match.DisplayName = type.DisplayName;
                match.Description = type.Description;
                match.Fields = type.Fields;
                match.Lifecycle = type.Lifecycle;
                // Carried like every other attribute of the schema. Dropping it silently reverted an
                // exported type to not-deliverable, so a round trip through export/import took the
                // content off the public API with the import still reporting success.
                match.IsPubliclyDeliverable = type.IsPubliclyDeliverable;
                match.IsSingleton = type.IsSingleton;
                match.UpdatedAt = DateTimeOffset.UtcNow;
                toStore.Add(match);
            }
            else
            {
                report.ContentTypesCreated++;

                var definition = new ContentTypeDefinition
                {
                    Id = Guid.NewGuid(),
                    // Normalized, like the create endpoint. Storing the file's spelling let an
                    // import put "Article" beside an existing "article": distinct to the unique
                    // index, the same to every reader.
                    Name = barakoCMS.Core.ContentTypeName.Normalize(type.Name),
                    DisplayName = type.DisplayName,
                    Description = type.Description,
                    Fields = type.Fields,
                    Lifecycle = type.Lifecycle,
                    IsPubliclyDeliverable = type.IsPubliclyDeliverable,
                    IsSingleton = type.IsSingleton,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };

                // Added to the lookup whether or not this is a dry run, so records later in the same
                // bundle resolve against a type this bundle creates.
                existing.Add(definition);
                toStore.Add(definition);
                created.Add(definition);
            }
        }

        // Each record is checked against its type as this bundle leaves it, not as it is stored.
        // No singleton cap: an import is a restore and lands what the bundle holds.
        var creator = Resolve<IContentCreator>();
        var batch = new ContentCreateBatch { CapSingletons = false };
        foreach (var definition in existing)
            batch.UseSchema(definition);

        var staged = new List<ContentCreateRequest>();
        for (var i = 0; i < records.Count; i++)
        {
            var rec = records[i];
            if (string.IsNullOrWhiteSpace(rec.ContentType)) continue;
            report.ContentsCreated++;

            // A record whose type is in neither the store nor the bundle gets no public fields, so
            // its SearchText comes out empty and it is unsearchable while the import still reports
            // success. Counted and named in the report rather than left to be discovered later.
            if (!existing.Any(t => t.Name.Equals(rec.ContentType, StringComparison.OrdinalIgnoreCase)))
            {
                report.ContentsWithoutContentType++;
                if (!report.UnknownContentTypes.Contains(rec.ContentType, StringComparer.OrdinalIgnoreCase))
                {
                    report.UnknownContentTypes.Add(rec.ContentType);
                }
            }

            var key = $"contents[{i}]";
            if (rec.Sensitivity is { } level && !Enum.IsDefined(level))
            {
                AddError(new ValidationFailure(key, "sensitivity is not a valid value."));
                continue;
            }

            var data = rec.Data ?? new Dictionary<string, object>();

            // A field the exporter could not read arrives holding its mask, not its value.
            foreach (var masked in rec.MaskedFields ?? [])
                data.Remove(masked);

            var request = new ContentCreateRequest
            {
                ContentType = rec.ContentType,
                Data = data,
                Status = Enum.TryParse<ContentStatus>(rec.Status, ignoreCase: true, out var s) && Enum.IsDefined(s)
                    ? s
                    : ContentStatus.Published,
                Sensitivity = rec.Sensitivity ?? SensitivityLevel.Public,
            };

            foreach (var error in await creator.CheckAsync(request, userId, HttpContext, batch, ct))
                AddError(new ValidationFailure(key, error));

            staged.Add(request);
        }

        ThrowIfAnyErrors();

        if (!req.DryRun)
        {
            foreach (var definition in toStore)
                _session.Store(definition);

            // Recorded here as well as in the create endpoint, because this is the other way a
            // content type comes into existence. A type with no policy row reads as not event
            // sourced, which is the right answer, but nothing stops the name being claimed as event
            // sourced later. DecideAsync never overwrites, so a name that already has a decision
            // keeps it.
            foreach (var definition in created)
                await Resolve<IContentSourcingPolicy>().DecideAsync(definition.Name, false, ct);

            foreach (var request in staged)
                await creator.StageAsync(request, userId, batch, ct);

            await AuditLog.RecordAsync(_session, _tenant.Slug, "portability.imported", userId, User.FindFirst("Username")?.Value,
                metadata: new()
                {
                    ["contentTypesCreated"] = report.ContentTypesCreated,
                    ["contentTypesUpdated"] = report.ContentTypesUpdated,
                    ["contentsCreated"] = report.ContentsCreated,
                }, ct: ct);
            await _session.SaveChangesAsync(ct);
        }
        await Send.ResponseAsync(report, cancellation: ct);
    }

    /// <summary>
    /// What the content-type endpoints would refuse about writing <paramref name="type"/> over
    /// <paramref name="stored"/>, or as a new type when <paramref name="stored"/> is null.
    /// </summary>
    private async Task<List<string>> TypeErrorsAsync(
        ContentTypeDefinition type, ContentTypeDefinition? stored, int maxFields, CancellationToken ct)
    {
        var validator = Resolve<IContentTypeValidatorService>();

        var (_, errors) = validator.Validate(type.Name, type.DisplayName, type.Fields);

        // The cap was settled above, including its one exception: a type already stored over it may
        // be imported again at its size. The validator only knows the cap, so its refusal of that
        // case is dropped here, and it is the only error it returns for an oversized list.
        if (type.Fields.Count > maxFields)
            errors.Remove(barakoCMS.Infrastructure.Services.ContentTypeFieldLimit.TooMany(maxFields, type.Fields.Count));

        errors.AddRange(validator.ValidateLifecycle(type.Lifecycle).Errors);

        var name = stored?.Name ?? barakoCMS.Core.ContentTypeName.Normalize(type.Name);
        var nonPublic = type.Fields.Where(f => f.Sensitivity != SensitivityLevel.Public).Select(f => f.Name).ToList();
        if (nonPublic.Count > 0 && await Resolve<IContentSourcingPolicy>().IsEventSourcedAsync(name, ct))
        {
            errors.Add($"'{name}' is event sourced, so its fields have to stay Public, and "
                       + $"{string.Join(", ", nonPublic)} {(nonPublic.Count == 1 ? "is" : "are")} not.");
        }

        if (stored is null)
            return errors;

        errors.AddRange(SensitivityChanges(type, stored));

        // The add field rule: a required field with no default declares an invariant every entry
        // already stored breaks.
        var newlyRequired = type.Fields
            .Where(f => f.IsRequired && f.DefaultValue is null)
            .Where(f => !stored.Fields.Any(s => s.IsRequired && s.Name.Equals(f.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (newlyRequired.Count > 0)
        {
            var lowered = stored.Name.ToLower();
            var entries = await _session.Query<Content>().CountAsync(c => c.ContentType.ToLower() == lowered, ct);
            if (entries > 0)
            {
                foreach (var field in newlyRequired)
                {
                    errors.Add($"it already has {entries} {(entries == 1 ? "entry" : "entries")}, so making "
                               + $"'{field.Name}' required with no default would make them all invalid. "
                               + "Give the field a defaultValue, or leave it optional.");
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// A bundle may not change who can read a stored field.
    /// </summary>
    /// <remarks>
    /// That is <c>PUT /api/content-types/{name}/fields/{field}/sensitivity</c>'s decision, behind its
    /// own capability, with an acknowledgement for lowering, its own audit entry, and a rebuild of
    /// the search text so a raised value stops matching anonymous search. An import carrying the
    /// change would skip all of that, so it is refused and the caller is pointed there. Leaving a
    /// non-Public field out of the bundle counts as a change: a key the schema does not declare is
    /// served unmasked.
    /// </remarks>
    private static IEnumerable<string> SensitivityChanges(ContentTypeDefinition type, ContentTypeDefinition stored)
    {
        foreach (var current in stored.Fields)
        {
            var incoming = type.Fields.FirstOrDefault(f => f.Name.Equals(current.Name, StringComparison.OrdinalIgnoreCase));
            var route = $"PUT /api/content-types/{stored.Name}/fields/{current.Name}/sensitivity";

            if (incoming is null)
            {
                if (current.Sensitivity != SensitivityLevel.Public)
                {
                    yield return $"field '{current.Name}' is {current.Sensitivity} and the bundle leaves it out, "
                                 + "which would serve the values already stored in it unmasked. Keep the "
                                 + $"field, or lower it with {route} first.";
                }

                continue;
            }

            if (incoming.Sensitivity != current.Sensitivity)
            {
                yield return $"field '{current.Name}' is {current.Sensitivity} and the bundle makes it "
                             + $"{incoming.Sensitivity}. An import does not change who may read a field; use {route}.";
            }
            else if (current.Sensitivity != SensitivityLevel.Public
                     && (incoming.Mask != current.Mask || !SameRoles(incoming.VisibleToRoles, current.VisibleToRoles)))
            {
                yield return $"field '{current.Name}' has a different mask or visibleToRoles in the bundle. "
                             + $"An import does not change who may read a field; use {route}.";
            }
        }
    }

    private static bool SameRoles(List<string>? a, List<string>? b) =>
        (a ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b ?? []);

    private static string Shorten(string name) => name.Length > 100 ? name[..100] + "..." : name;
}
