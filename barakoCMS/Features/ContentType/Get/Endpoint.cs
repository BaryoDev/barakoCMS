using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.ContentType.Get;

public class Response : List<ContentTypeDefinition> { }

public class Endpoint : EndpointWithoutRequest<Response>
{
    private readonly IQuerySession _session;

    public Endpoint(IQuerySession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Get("/api/schemas");
        AllowAnonymous(); // Or restrict to Authenticated users? For the Builder UI, probably Auth only, but for usage... let's say Auth.
        // Actually, for "Zero Code" frontend, we might need public schema? Maybe not.
        Roles("Admin", "Editor");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var methods = await _session.Query<ContentTypeDefinition>()
            .OrderBy(x => x.Name)
            .ToListAsync(ct);

        var response = new Response();
        response.AddRange(methods);
        await SendOkAsync(response, ct);
    }
}
