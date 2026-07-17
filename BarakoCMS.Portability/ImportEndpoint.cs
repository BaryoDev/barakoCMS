using barakoCMS.Events;
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
    public ImportEndpoint(IDocumentSession session) => _session = session;

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

        var existing = await _session.Query<ContentTypeDefinition>().ToListAsync(ct);
        foreach (var type in req.ContentTypes)
        {
            if (string.IsNullOrWhiteSpace(type.Name)) continue;
            var match = existing.FirstOrDefault(t => t.Name.Equals(type.Name, StringComparison.OrdinalIgnoreCase));
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
                    _session.Store(new ContentTypeDefinition
                    {
                        Id = Guid.NewGuid(),
                        Name = type.Name,
                        DisplayName = type.DisplayName,
                        Description = type.Description,
                        Fields = type.Fields,
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    });
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
                var evt = new ContentCreated(contentId, rec.ContentType, rec.Data, status, userId);
                _session.Events.StartStream<barakoCMS.Models.Content>(contentId, evt);
                var content = new barakoCMS.Models.Content();
                content.Apply(evt);
                _session.Store(content);
            }
        }

        if (!req.DryRun) await _session.SaveChangesAsync(ct);
        await SendAsync(report, cancellation: ct);
    }
}
