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
        // NOTE: AllowAnonymous() must NOT be combined with Roles() — in ASP.NET Core
        // AllowAnonymous short-circuits authorization and silently disables the role check.
        Roles("SuperAdmin", "Admin", "Editor");
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
