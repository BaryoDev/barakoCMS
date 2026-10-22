using barakoCMS.Infrastructure.Auth;
using FastEndpoints;

namespace BarakoCMS.Testing.Fixture;

/// <summary>GET /api/fixture/ping, gated on <see cref="FixtureCapabilities.ViewFixture"/>.</summary>
public sealed class FixturePingEndpoint : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("/api/fixture/ping");
        Definition.RequireCapability(FixtureCapabilities.ViewFixture);
    }

    public override Task HandleAsync(CancellationToken ct) => Send.OkAsync(ct);
}
