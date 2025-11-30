using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Diagnostics;

public class TypeCheckEndpoint : EndpointWithoutRequest
{
    private readonly IDocumentSession _session;

    public TypeCheckEndpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Get("/api/diagnostics/typecheck");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var propType = typeof(IDocumentSession).GetProperty("Events")?.PropertyType.FullName;
        await SendAsync(new { PropertyType = propType });
    }
}
