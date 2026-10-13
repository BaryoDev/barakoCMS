using System.Net;
using barakoCMS.Features.Monitoring.Meta;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// <see cref="ApiContract.HeaderName"/> is how a console learns the HTTP contract version without
/// signing in first. <c>GET /api/meta</c> requires a session, so the header, not the response body,
/// is the only thing that answers "will this console work here" before authentication and again
/// mid-session if a rolling upgrade moves the contract underneath an open one.
/// </summary>
[Collection("Sequential")]
public class ApiContractVersionHeaderTests
{
    private readonly IntegrationTestFixture _factory;

    public ApiContractVersionHeaderTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task An_ordinary_response_carries_the_api_contract_version_header()
    {
        var response = await _factory.CreateClient().GetAsync("/health");

        response.Headers.TryGetValues(ApiContract.HeaderName, out var values).Should().BeTrue(
            "a console has to be able to read this from whatever call it makes first, not only from "
          + "/api/meta");
        values!.Should().ContainSingle().Which.Should().Be(ApiContract.Version.ToString());
    }

    // The case the issue is actually about: /api/meta itself answers 401 to an anonymous caller,
    // so if the header were only set on success this would be the one response a console cannot
    // read it from before signing in.
    [Fact]
    public async Task The_header_is_present_even_on_the_meta_endpoints_own_401()
    {
        var response = await _factory.CreateClient().GetAsync("/api/meta");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.TryGetValues(ApiContract.HeaderName, out var values).Should().BeTrue(
            "an anonymous caller hitting the one endpoint that reports the contract version in its "
          + "body is exactly who the header exists for");
        values!.Should().ContainSingle().Which.Should().Be(ApiContract.Version.ToString());
    }
}
