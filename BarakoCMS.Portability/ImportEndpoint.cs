using barakoCMS.Core.Interfaces;
using barakoCMS.Events;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Portability;

/// <summary>
/// POST /api/portability/import — upsert content types (by name) then recreate content via events.
/// Pass <c>dryRun: true</c> to preview the counts without writing.
/// </summary>
public class ImportEndpoint : Endpoint<ImportRequest, ImportReport>
{
    private readonly IDocumentSession _session;
    private readonly IContentWriter _contentWriter;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

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

                if (!req.DryRun)
                {
                    match.DisplayName = type.DisplayName;
                    match.Description = type.Description;
                    match.Fields = type.Fields;
                    match.UpdatedAt = DateTimeOffset.UtcNow;
                    _session.Store(match);
                }
            }
            else
            {
                report.ContentTypesCreated++;

                if (!req.DryRun)
                {
                    var definition = new ContentTypeDefinition
                    {
                        Id = Guid.NewGuid(),
                        Name = type.Name,
                        DisplayName = type.DisplayName,
                        Description = type.Description,
                        Fields = type.Fields,
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };

                    _session.Store(definition);
                    existing.Add(definition);
                }
            }
        }

        foreach (var rec in req.Contents)
        {
            if (string.IsNullOrWhiteSpace(rec.ContentType)) continue;
            report.ContentsCreated++;
            if (!req.DryRun)
            {
                var status = Enum.TryParse<ContentStatus>(rec.Status, ignoreCase: true, out var s) ? s : ContentStatus.Published;
                var contentId = Guid.NewGuid();
                var definition = existing.FirstOrDefault(t =>
                    t.Name.Equals(rec.ContentType, StringComparison.OrdinalIgnoreCase));

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
                _contentWriter.Create(evt);
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
        await SendAsync(report, cancellation: ct);
    }
}
