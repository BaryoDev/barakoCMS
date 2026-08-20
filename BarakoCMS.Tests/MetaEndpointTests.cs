using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

[Collection("Sequential")]
public class MetaEndpointTests
{
    private readonly IntegrationTestFixture _factory;

    public MetaEndpointTests(IntegrationTestFixture factory)
    {
        _factory = factory;
    }

    private sealed record Meta(string Version, bool SwaggerEnabled);

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/meta");

        // Exactly 401, not "any non-200". A 404 would satisfy a looser assertion while meaning the
        // endpoint is simply absent, which proves nothing about whether it is protected.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // The positive control for the test above. Without it, deleting the endpoint entirely, or
    // refusing every caller, would leave the refusal test green.
    [Fact]
    public async Task A_signed_in_caller_gets_the_running_version()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.CreateToken(new[] { "Admin" }));

        var response = await client.GetAsync("/api/meta");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var meta = await response.Content.ReadFromJsonAsync<Meta>();
        meta.Should().NotBeNull();
        meta!.Version.Should().NotBeNullOrWhiteSpace();
        meta.Version.Should().NotBe("unknown");
        // Guards the InformationalVersion trimming: a build with SourceLink active produces
        // "3.21.0+abc1234", and shipping that to the About dialog would be wrong.
        meta.Version.Should().NotContain("+");
    }

    // Any authenticated backoffice user, not just an admin. The version answers "what am I running"
    // and every signed-in user needs it; restricting by role was considered and rejected.
    [Fact]
    public async Task A_non_admin_role_may_also_read_it()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _factory.CreateToken(new[] { "Editor" }));

        var response = await client.GetAsync("/api/meta");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
