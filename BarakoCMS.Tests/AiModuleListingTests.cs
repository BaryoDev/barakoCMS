using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BarakoCMS.AI;
using barakoCMS.Modules;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Issue #1254: adding the completion client changed nothing the modules listing says about the AI module.
/// The API reports it as "AI" (AiModule.Name); the package is BarakoCMS.AI.
/// </summary>
[Collection("Integration1")]
public class AiModuleListingTests
{
    private readonly IntegrationTestFixture _factory;

    public AiModuleListingTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task GET_api_modules_reports_the_AI_module_as_before()
    {
        var module = new AiModule();
        var host = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton(ModuleCatalogue.Of([module], [module]))));
        var client = await AdminClientAsync(host);

        var response = await client.GetAsync("/api/modules", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToArray();
        var ai = items.Should().ContainSingle().Which;
        ai.GetProperty("name").GetString().Should().Be("AI");
        ai.GetProperty("packageId").GetString().Should().Be("BarakoCMS.AI");
        ai.GetProperty("category").GetString().Should().Be("Insight");
        ai.GetProperty("description").GetString().Should().Be("Semantic search over published content");
        ai.GetProperty("glyph").GetString().Should().Be("sparkles");
        ai.GetProperty("enabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void The_module_declares_no_configuration_keys_so_no_completion_key_can_be_shown()
    {
        ((IBarakoModule)new AiModule()).ConfigurationView.Should().BeNull();
    }

    private async Task<HttpClient> AdminClientAsync(WebApplicationFactory<Program> host)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var roleIds = new List<Guid>();
        foreach (var roleName in new[] { "SuperAdmin", "Admin" })
        {
            var role = await session.Query<barakoCMS.Models.Role>().FirstOrDefaultAsync(r => r.Name == roleName);
            if (role is null)
            {
                role = new barakoCMS.Models.Role { Id = Guid.NewGuid(), Name = roleName };
                session.Store(role);
            }
            roleIds.Add(role.Id);
        }

        var userId = Guid.NewGuid();
        session.Store(new barakoCMS.Models.User
        {
            Id = userId,
            Username = $"ailist-{userId:n}",
            Email = $"ailist-{userId:n}@example.com",
            RoleIds = roleIds,
        });
        await session.SaveChangesAsync();

        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: ["SuperAdmin", "Admin"], userId: userId.ToString()));
        return client;
    }
}
