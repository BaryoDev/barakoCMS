using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Content.Get;

public class Endpoint : Endpoint<Request, Response>
{
    private readonly IQuerySession _session;

    public Endpoint(IQuerySession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Get("/api/contents/{id}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        var content = await _session.LoadAsync<barakoCMS.Models.Content>(req.Id, ct);

        if (content == null)
        {
            await SendNotFoundAsync(ct);
            return;
        }

        Response = new Response
        {
            Id = content.Id,
            ContentType = content.ContentType,
            Data = new Dictionary<string, object>(content.Data),
            CreatedAt = content.CreatedAt,
            UpdatedAt = content.UpdatedAt,
            LastModifiedBy = content.LastModifiedBy,
            Sensitivity = content.Sensitivity
        };

        var sensitivityService = Resolve<barakoCMS.Core.Interfaces.ISensitivityService>();
        sensitivityService.Apply(Response, HttpContext);
    }
}
