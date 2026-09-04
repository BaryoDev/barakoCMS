using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using Marten;

namespace BarakoCMS.Forms.Features.Definitions.Get;

/// <summary>GET /api/forms/{name}.</summary>
public class Endpoint : EndpointWithoutRequest<FormResponse>
{
    private readonly IQuerySession _session;

    public Endpoint(IQuerySession session) => _session = session;

    public override void Configure()
    {
        Get("/api/forms/{name}");
        Definition.RequireCapability(FormsCapabilities.ManageForms, FormsCapabilities.LegacyRoles);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var name = Route<string>("name") ?? string.Empty;
        var form = await _session.Query<FormDefinition>().FirstOrDefaultAsync(f => f.Name == name, ct);
        if (form is null) { await Send.NotFoundAsync(ct); return; }

        await Send.ResponseAsync(FormResponse.From(form), cancellation: ct);
    }
}
