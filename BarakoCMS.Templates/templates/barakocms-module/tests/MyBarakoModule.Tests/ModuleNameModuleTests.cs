using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.Testing;
using Xunit;

namespace MyBarakoModule.Tests;

/// <summary>A barakoCMS running this module, over a PostgreSQL that Testcontainers starts. One per test class.</summary>
public sealed class ModuleNameHost : BarakoTestHost
{
    public ModuleNameHost() : base(o =>
    {
        o.Modules.Add(new ModuleNameModule());
        o.Settings["Modules:ModuleName:Greeting"] = "Hello from the test";
    })
    {
    }
}

public class ModuleNameModuleTests : IClassFixture<ModuleNameHost>
{
    private readonly ModuleNameHost _host;

    public ModuleNameModuleTests(ModuleNameHost host) => _host = host;

    [Fact]
    public async Task The_module_loads_and_its_endpoint_answers()
    {
        await using (var session = _host.OpenSession())
        {
            session.Store(new Note { Id = Guid.NewGuid(), Title = "first" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var client = await _host.CreateAdminClientAsync(TestContext.Current.CancellationToken);
        var response = await client.GetAsync("/api/modulename/notes", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("Hello from the test", body.GetProperty("greeting").GetString());
        Assert.Contains(body.GetProperty("items").EnumerateArray(), n => n.GetProperty("title").GetString() == "first");
    }

    [Fact]
    public async Task An_Admin_reaches_it_through_the_capability_the_seeder_granted()
    {
        // Admin alone: the seeded admin is also SuperAdmin, whose wildcard would pass any gate.
        var admin = await _host.CreateClientAsync("Admin", TestContext.Current.CancellationToken);

        var response = await admin.GetAsync("/api/modulename/notes", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task An_anonymous_caller_is_refused()
    {
        var response = await _host.CreateClient().GetAsync("/api/modulename/notes", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
