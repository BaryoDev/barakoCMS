using barakoCMS.Infrastructure.Auth.Mfa;
using FastEndpoints;

namespace BarakoCMS.Tests.Mfa;

/// <summary>
/// A sensitive action that exists only in the test host. FastEndpoints scans this assembly because it
/// is loaded, the same way JobTestEndpoints are served.
/// </summary>
internal sealed class StepUpTestEndpoint : EndpointWithoutRequest
{
    public override void Configure()
    {
        Post("/api/_test/step-up");
        Claims("UserId");
        Definition.RequireStepUp();
        Description(b => b.ExcludeFromDescription());
    }

    public override Task HandleAsync(CancellationToken ct) => Send.NoContentAsync(ct);
}
