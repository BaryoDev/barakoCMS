using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Marten;
using Microsoft.Extensions.Options;

namespace MyBarakoModule.Features.Notes.List;

/// <summary>GET /api/modulename/notes: the tenant's notes, newest first, one page at a time.</summary>
internal sealed class Endpoint : Endpoint<Request, Response>
{
    private readonly IQuerySession _session;
    private readonly IOptions<ModuleNameOptions> _options;

    public Endpoint(IQuerySession session, IOptions<ModuleNameOptions> options)
    {
        _session = session;
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
        // The session was opened for the request's tenant, so this query cannot see another
        // tenant's notes and nothing here has to filter by tenant.
        var total = await _session.Query<Note>().CountAsync(ct);
        var notes = await _session.Query<Note>()
            .OrderByDescending(n => n.CreatedAt)
            .Skip(req.Skip)
            .Take(req.Take)
            .ToListAsync(ct);

        await Send.OkAsync(new Response(
            _options.Value.Greeting,
            notes.Select(n => new NoteSummary(n.Id, n.Title, n.CreatedAt)).ToList(),
            req.Page,
            req.PageSize,
            total), ct);
    }
}
