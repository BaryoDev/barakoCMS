using barakoCMS.Features.ContentType.ChangeField;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.ContentType.FieldChangeStatus;

internal sealed class Request
{
    public Guid Id { get; set; }
}

/// <summary>GET /api/content-types/field-changes/{id}: where a field change is.</summary>
/// <remarks>
/// A change stopped part way shows the step it is on, how many entries it has rewritten and the
/// error that stopped it. Another tenant's change, like one that does not exist, is a 404.
/// </remarks>
internal sealed class Endpoint(IQuerySession session) : Endpoint<Request, FieldChangeResponse>
{
    public override void Configure()
    {
        Get("/api/content-types/field-changes/{id}");
        Definition.RequireCapability(SystemCapabilities.ManageContentTypes, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        if (await session.LoadAsync<FieldChange>(req.Id, ct) is not { } change)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(FieldChangeResponse.From(change), ct);
    }
}
