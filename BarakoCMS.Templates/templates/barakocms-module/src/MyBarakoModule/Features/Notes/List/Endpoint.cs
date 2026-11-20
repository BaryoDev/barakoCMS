using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Microsoft.Extensions.Options;

namespace MyBarakoModule.Features.Notes.List;

/// <summary>GET /api/modulename/notes: the tenant's notes, newest first, one page at a time.</summary>
internal sealed class Endpoint : Endpoint<Request, Response>
{
    private readonly IContentReader _reader;
    private readonly IOptions<ModuleNameOptions> _options;

    public Endpoint(IContentReader reader, IOptions<ModuleNameOptions> options)
    {
        _reader = reader;
        _options = options;
    }

    public override void Configure()
    {
        Get("/api/modulename/notes");

        // A capability the module declares, never Roles(...). An anonymous caller is refused with
        // 401 before this runs; a signed-in caller without the capability gets 403.
        Definition.RequireCapability(ModuleNameCapabilities.ReadNotes);
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        // The reader works over the session opened for the request's tenant, so it cannot see
        // another tenant's notes and nothing here has to filter by tenant.
        var total = await _reader.CountAsync(ModuleNameContentTypes.Note, ct);

        // The reader lists oldest first, so the newest page is read from the far end and turned round.
        var remaining = Math.Max(0, total - req.Skip);
        var take = Math.Min(req.Take, remaining);
        List<barakoCMS.Models.Content> notes = [];
        if (take > 0)
        {
            notes = (await _reader.ListAsync(ModuleNameContentTypes.Note, remaining - take, take, ct))
                .OrderByDescending(n => n.CreatedAt).ToList();
        }

        await Send.OkAsync(new Response(
            _options.Value.Greeting,
            notes.Select(n => new NoteSummary(n.Id, TitleOf(n.Data), n.CreatedAt)).ToList(),
            req.Page,
            req.PageSize,
            total), ct);
    }

    // A JSON client usually sends camelCase keys and the data keeps whatever arrived, so match the
    // field name without regard to case, as the schema validator does.
    private static string TitleOf(IReadOnlyDictionary<string, object> data) =>
        data.FirstOrDefault(kv => string.Equals(kv.Key, "Title", StringComparison.OrdinalIgnoreCase))
            .Value?.ToString() ?? string.Empty;
}
