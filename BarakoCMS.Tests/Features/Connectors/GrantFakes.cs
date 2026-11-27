using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>Counts what a Request step asked to send, and keeps the last request.</summary>
internal sealed class GrantCountingSender : IConnectorSender
{
    public int Sent { get; private set; }

    public ComposedRequest? Last { get; private set; }

    public Task<ConnectorCallResult> ProbeAsync(Connector connector, CancellationToken ct) =>
        Task.FromResult(new ConnectorCallResult(true, 200, 1, null));

    public Task<ConnectorCallResult> SendAsync(
        Connector connector, ComposedRequest request, SuccessRule rule, string? successJsonPath, CancellationToken ct)
    {
        Sent++;
        Last = request;
        return Task.FromResult(new ConnectorCallResult(true, 200, 1, null));
    }
}

/// <summary>Counts what a collection sync asked to fetch, and keeps the last request.</summary>
internal sealed class GrantCountingFetcher : IConnectorFetcher
{
    public int Fetched { get; private set; }

    public ComposedRequest? Last { get; private set; }

    public Task<ConnectorFetchResult> FetchAsync(Connector? connector, ComposedRequest request, int maxBytes, CancellationToken ct)
    {
        Fetched++;
        Last = request;
        return Task.FromResult(new ConnectorFetchResult(true, 200, 1, null, "[]"));
    }
}
