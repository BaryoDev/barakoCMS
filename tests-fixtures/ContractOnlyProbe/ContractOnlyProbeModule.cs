using barakoCMS.Modules;
using FastEndpoints;

namespace ContractOnlyProbe;

/// <summary>A module and an endpoint compiled against BarakoCMS.Abstractions alone.</summary>
public sealed class ContractOnlyProbeModule : IBarakoModule
{
    public const string ModuleName = "Contract Only Probe";

    public string Name => ModuleName;
}

/// <summary>Answers without auth, so the test needs nothing but the host.</summary>
public sealed class ContractOnlyPingEndpoint : EndpointWithoutRequest<ContractOnlyPingResponse>
{
    public const string Route = "/api/probe/contract-only/ping";

    public override void Configure()
    {
        Get(Route);
        AllowAnonymous();
    }

    public override Task HandleAsync(CancellationToken ct) =>
        Send.OkAsync(new ContractOnlyPingResponse("pong"), ct);
}

public sealed record ContractOnlyPingResponse(string Answer);
