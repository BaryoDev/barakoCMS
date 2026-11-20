using System.Net;
using System.Text.Json;
using ContractOnlyProbe;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A module compiled against BarakoCMS.Abstractions alone, with no reference to core (#972): the
/// host still maps its endpoint. <c>ContractOnlyProbe</c> is that module.
/// </summary>
/// <remarks>
/// The host's endpoint scan runs <c>LoadsOrIsSkipped</c> on every loaded assembly and probes the
/// ones that reference core or the contract. A scan that only recognised core-reaching assemblies
/// would leave a core-less module's endpoints unmapped and answer 404.
/// </remarks>
[Collection("Integration1")]
public class ContractOnlyModuleEndpointTests
{
    private readonly IntegrationTestFixture _factory;

    public ContractOnlyModuleEndpointTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task An_endpoint_of_a_module_with_no_core_reference_answers()
    {
        typeof(ContractOnlyPingEndpoint).Assembly.GetReferencedAssemblies().Select(a => a.Name)
            .Should().NotContain("barakoCMS", "the control: the probe never compiled against core");

        var response = await _factory.CreateClient()
            .GetAsync(ContractOnlyPingEndpoint.Route, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("answer").GetString().Should().Be("pong");
    }
}
