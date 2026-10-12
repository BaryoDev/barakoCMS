using System.Net;
using Alba;
using barakoCMS.Features.Monitoring.Meta;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// <see cref="ApiContract.HeaderName"/> is how a console learns the HTTP contract version without
/// signing in first. <c>GET /api/meta</c> requires a session, so the header, not the response body,
/// is the only thing that answers "will this console work here" before authentication and again
/// mid-session if a rolling upgrade moves the contract underneath an open one.
/// </summary>
/// <remarks>
/// The Alba pilot (#1024): these run as Alba scenarios on an <see cref="AlbaHost"/> built with the
/// shared fixture's own host configuration, against the same database. Building a second host
/// replaces FastEndpoints' process-wide service resolver, so its lifetime sits inside
/// <see cref="FastEndpointsResolver.Keep"/>, which puts the fixture's resolver back when it ends.
/// docs/internal/alba-pilot.md has the comparison and the recommendation.
/// </remarks>
[Collection("Sequential")]
public class ApiContractVersionHeaderTests(IntegrationTestFixture factory) : IAsyncLifetime
{
    private IDisposable? _resolver;
    private IAlbaHost? _host;

    private IAlbaHost Host => _host ?? throw new InvalidOperationException("The Alba host has not started.");

    public async ValueTask InitializeAsync()
    {
        _resolver = FastEndpointsResolver.Keep();
        _host = await AlbaHost.For<Program>(factory.ApplyTestHostConfiguration);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        _resolver?.Dispose();
    }

    // The status is not part of this claim and never was, so it is ignored rather than left to
    // Alba's default of expecting a 200.
    [Fact]
    public Task An_ordinary_response_carries_the_api_contract_version_header() =>
        Host.Scenario(s =>
        {
            s.Get.Url("/health");
            s.IgnoreStatusCode();
            s.Header(ApiContract.HeaderName).SingleValueShouldEqual(ApiContract.Version.ToString());
        });

    // The case the issue is actually about: /api/meta itself answers 401 to an anonymous caller,
    // so if the header were only set on success this would be the one response a console cannot
    // read it from before signing in.
    [Fact]
    public Task The_header_is_present_even_on_the_meta_endpoints_own_401() =>
        Host.Scenario(s =>
        {
            s.Get.Url("/api/meta");
            s.StatusCodeShouldBe(HttpStatusCode.Unauthorized);
            s.Header(ApiContract.HeaderName).SingleValueShouldEqual(ApiContract.Version.ToString());
        });
}
