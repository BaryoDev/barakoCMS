using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Portability;

/// <summary>
/// POST /api/portability/import. Upserts content types (by name) then recreates content via events.
/// Pass <c>dryRun: true</c> to preview the counts without writing.
/// </summary>
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
        Roles("SuperAdmin", "Admin");
        Claims("UserId");
    }

    public override async Task HandleAsync(ImportRequest req, CancellationToken ct)
    {
        Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId);
        var report = new ImportReport { DryRun = req.DryRun };

        var existing = (await _session.Query<ContentTypeDefinition>().ToListAsync(ct)).ToList();
        foreach (var type in req.ContentTypes)
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
                // Carried like every other attribute of the schema. Dropping it silently reverted an
                // exported type to not-deliverable, so a round trip through export/import took the
                // content off the public API with the import still reporting success.
                match.IsPubliclyDeliverable = type.IsPubliclyDeliverable;
                match.UpdatedAt = DateTimeOffset.UtcNow;

                if (!req.DryRun)
                {
                    _session.Store(match);
                }
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
                    IsPubliclyDeliverable = type.IsPubliclyDeliverable,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };

                // Added to the lookup whether or not this is a dry run, so records later in the same
                // bundle resolve against a type this bundle creates. Without it the dry run reports
                // every one of them as having no schema, and the real run indexed them against no
                // fields at all.
                existing.Add(definition);

                if (!req.DryRun)
                {
                    _session.Store(definition);

                    // Recorded here as well as in the create endpoint, because this is the other way
                    // a content type comes into existence. A type with no policy row reads as not
                    // event sourced, which is the right answer, but nothing stops the name being
                    // claimed as event sourced later. DecideAsync never overwrites, so a name that
                    // already has a decision keeps it.
                    await Resolve<IContentSourcingPolicy>().DecideAsync(definition.Name, false, ct);
                }
            }
        }

        foreach (var rec in req.Contents)
        {
            if (string.IsNullOrWhiteSpace(rec.ContentType)) continue;
            report.ContentsCreated++;

            var definition = existing.FirstOrDefault(t =>
                t.Name.Equals(rec.ContentType, StringComparison.OrdinalIgnoreCase));

            // A record whose type is in neither the store nor the bundle gets no public fields, so
            // its SearchText comes out empty and it is unsearchable while the import still reports
            // success. Counted and named in the report rather than left to be discovered later.
            if (definition is null)
            {
                report.ContentsWithoutContentType++;
                if (!report.UnknownContentTypes.Contains(rec.ContentType, StringComparer.OrdinalIgnoreCase))
                {
                    report.UnknownContentTypes.Add(rec.ContentType);
                }
            }

            if (!req.DryRun)
            {
                var status = Enum.TryParse<ContentStatus>(rec.Status, ignoreCase: true, out var s) ? s : ContentStatus.Published;
                var contentId = Guid.NewGuid();

                var publicFields = definition?.Fields
                    .Where(f => f.Sensitivity == SensitivityLevel.Public)
                    .Select(f => f.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var searchText = string.Join(
                    ' ',
                    rec.Data
                        .Where(kv => publicFields.Contains(kv.Key))
                        .Select(kv => kv.Value?.ToString())
                        .Where(v => !string.IsNullOrWhiteSpace(v)));

                var evt = new ContentCreated(contentId, rec.ContentType, rec.Data, status, userId, searchText, barakoCMS.Models.SensitivityLevel.Public);
                await _contentWriter.CreateAsync(evt, ct);
            }
        }

        if (!req.DryRun)
        {
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
}
