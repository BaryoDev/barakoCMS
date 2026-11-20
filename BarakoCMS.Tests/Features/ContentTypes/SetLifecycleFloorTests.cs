using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Models;
using barakoCMS.Modules;
using BarakoCMS.Tests.Features.Tenants;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// The floor a module's seed puts under a lifecycle edit: what the module seeded stays, and adding
/// states and transitions stays allowed.
/// </summary>
/// <remarks>
/// A test module seeds the type, through the host's own seed runner, so the record the route reads
/// is the one a real seed writes.
/// </remarks>
[Collection("Integration1")]
public class SetLifecycleFloorTests(IntegrationTestFixture factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDocumentStore Store => factory.Services.GetRequiredService<IDocumentStore>();

    private static LifecycleDefinition Seeded() => new()
    {
        States = ["Draft", "Review", "Done"],
        InitialState = "Draft",
        Transitions =
        [
            new StateTransition { Name = "Submit", From = "Draft", To = "Review" },
            new StateTransition { Name = "Finish", From = "Review", To = "Done" },
        ],
    };

    private sealed class FloorModule(string type, string name = "FloorTest") : IBarakoModule
    {
        public string Name => name;

        public async Task SeedAsync(IModuleSeedContext context, CancellationToken ct) =>
            await context.EnsureContentTypeAsync(
                new ContentTypeDefinition { Id = Guid.NewGuid(), Name = type, DisplayName = type, Lifecycle = Seeded() }, ct);
    }

    private static string NewName() => "floor" + Guid.NewGuid().ToString("n")[..10];

    /// <summary>A host that has the test module registered, and has run its seed.</summary>
    private async Task<WebApplicationFactory<Program>> HostWithAsync(string type, bool seed = true, string module = "FloorTest")
    {
        var host = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddSingleton<IBarakoModule>(new FloorModule(type, module))));
        if (seed)
        {
            await barakoCMS.Extensions.ServiceCollectionExtensions.RunBarakoModuleSeedersAsync(host.Services, Ct);
        }

        return host;
    }

    private async Task<HttpClient> AdminOnAsync(WebApplicationFactory<Program> host, HttpClient? signedIn = null)
    {
        var source = signedIn ?? await factory.CreateClientAsync("SuperAdmin", "Admin");
        var client = host.CreateClient();
        foreach (var header in source.DefaultRequestHeaders)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        }

        return client;
    }

    private async Task StoreTypeAsync(string type, string? tenant = null)
    {
        await using var session = tenant is null ? Store.LightweightSession() : Store.LightweightSession(tenant);
        session.Store(new ContentTypeDefinition { Id = Guid.NewGuid(), Name = type, DisplayName = type, Lifecycle = Seeded() });
        await session.SaveChangesAsync(Ct);
    }

    private async Task<HttpResponseMessage> PutAsync(
        HttpClient client, string type, LifecycleDefinition lifecycle, bool dryRun = false, string? tenant = null)
    {
        await using var session = tenant is null ? Store.QuerySession() : Store.QuerySession(tenant);
        var stored = await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type, Ct);
        var version = (await session.MetadataForAsync(stored, Ct)).CurrentVersion;
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/content-types/{type}/lifecycle")
        {
            Content = JsonContent.Create(new { lifecycle, dryRun }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{version:D}\"");
        return await client.SendAsync(request, Ct);
    }

    private async Task<List<JsonElement>> ModuleBlockersAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(status, text);
        var body = JsonDocument.Parse(text).RootElement;
        return body.GetProperty("blockers").EnumerateArray()
            .Where(b => b.GetProperty("kind").GetString() == "module").Select(b => b.Clone()).ToList();
    }

    private async Task<LifecycleDefinition> StoredLifecycleAsync(string type, string? tenant = null)
    {
        await using var session = tenant is null ? Store.QuerySession() : Store.QuerySession(tenant);
        return (await session.Query<ContentTypeDefinition>().SingleAsync(d => d.Name == type, Ct)).Lifecycle!;
    }

    [Fact]
    public async Task Edit_refuses_removing_a_seeded_transition_and_names_the_module()
    {
        var type = NewName();
        using var host = await HostWithAsync(type);
        var admin = await AdminOnAsync(host);
        var edited = Seeded();
        edited.Transitions.RemoveAt(1);

        var dry = await ModuleBlockersAsync(await PutAsync(admin, type, edited, dryRun: true), HttpStatusCode.OK);
        var refused = await ModuleBlockersAsync(await PutAsync(admin, type, edited), HttpStatusCode.Conflict);

        dry.Should().ContainSingle().Which.GetProperty("name").GetString().Should().Be("FloorTest");
        refused.Should().ContainSingle().Which.GetProperty("transition").GetString().Should().Be("Finish");
        (await StoredLifecycleAsync(type)).Transitions.Should().HaveCount(2);
    }

    [Fact]
    public async Task Edit_refuses_renaming_a_seeded_transition_only_in_case_and_names_the_module()
    {
        var type = NewName();
        using var host = await HostWithAsync(type);
        var admin = await AdminOnAsync(host);
        var edited = Seeded();
        edited.Transitions[1].Name = "FiniSh";

        var refused = await ModuleBlockersAsync(await PutAsync(admin, type, edited), HttpStatusCode.Conflict);

        refused.Should().ContainSingle().Which.GetProperty("name").GetString().Should().Be("FloorTest");
        (await StoredLifecycleAsync(type)).Transitions.Select(t => t.Name).Should().Contain("Finish");
    }

    [Fact]
    public async Task Edit_refuses_changing_the_from_or_to_of_a_seeded_transition()
    {
        var type = NewName();
        using var host = await HostWithAsync(type);
        var admin = await AdminOnAsync(host);
        var edited = Seeded();
        edited.Transitions[1].To = "Draft";

        var refused = await ModuleBlockersAsync(await PutAsync(admin, type, edited), HttpStatusCode.Conflict);

        refused.Should().ContainSingle().Which.GetProperty("transition").GetString().Should().Be("Finish");
    }

    [Fact]
    public async Task Edit_refuses_renaming_a_seeded_state()
    {
        var type = NewName();
        using var host = await HostWithAsync(type);
        var admin = await AdminOnAsync(host);
        var edited = Seeded();
        edited.States[2] = "Closed";
        edited.Transitions[1].To = "Closed";

        var refused = await ModuleBlockersAsync(await PutAsync(admin, type, edited), HttpStatusCode.Conflict);

        refused.Select(b => b.GetProperty("state").GetString()).Should().Contain("Done");
    }

    [Fact]
    public async Task Edit_refuses_changing_the_seeded_initial_state()
    {
        var type = NewName();
        using var host = await HostWithAsync(type);
        var admin = await AdminOnAsync(host);
        var edited = Seeded();
        edited.InitialState = "Review";

        var refused = await ModuleBlockersAsync(await PutAsync(admin, type, edited), HttpStatusCode.Conflict);

        refused.Should().ContainSingle().Which.GetProperty("state").GetString().Should().Be("Draft");
    }

    [Fact]
    public async Task Edit_allows_adding_a_state_and_a_transition_to_a_seeded_type()
    {
        var type = NewName();
        using var host = await HostWithAsync(type);
        var admin = await AdminOnAsync(host);
        var edited = Seeded();
        edited.States.Add("Archived");
        edited.Transitions.Add(new StateTransition { Name = "Archive", From = "Done", To = "Archived" });

        var response = await PutAsync(admin, type, edited);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await StoredLifecycleAsync(type)).States.Should().Contain("Archived");
    }

    [Fact]
    public async Task A_type_that_existed_before_the_seed_gets_its_record_on_the_next_run()
    {
        var type = NewName();
        await StoreTypeAsync(type);
        await using (var before = Store.QuerySession())
        {
            (await before.LoadAsync<ModuleSeededType>(ModuleSeededType.KeyOf(type), Ct)).Should().BeNull();
        }

        using var host = await HostWithAsync(type);

        await using var after = Store.QuerySession();
        var record = await after.LoadAsync<ModuleSeededType>(ModuleSeededType.KeyOf(type), Ct);
        record.Should().NotBeNull();
        record!.Module.Should().Be("FloorTest");
        record.States.Should().Equal("Draft", "Review", "Done");
        record.InitialState.Should().Be("Draft");
        record.Transitions.Select(t => (t.Name, t.From, t.To)).Should().Equal(("Submit", "Draft", "Review"), ("Finish", "Review", "Done"));
        var edited = Seeded();
        edited.Transitions.RemoveAt(1);
        var refused = await PutAsync(await AdminOnAsync(host), type, edited);
        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Seeding_again_writes_nothing_when_the_record_is_the_same()
    {
        var type = NewName();
        using var host = await HostWithAsync(type);
        Guid first;
        await using (var one = Store.QuerySession())
        {
            var record = await one.LoadAsync<ModuleSeededType>(ModuleSeededType.KeyOf(type), Ct);
            first = (await one.MetadataForAsync(record!, Ct)).CurrentVersion;
        }

        await barakoCMS.Extensions.ServiceCollectionExtensions.RunBarakoModuleSeedersAsync(host.Services, Ct);

        await using var two = Store.QuerySession();
        var again = await two.LoadAsync<ModuleSeededType>(ModuleSeededType.KeyOf(type), Ct);
        (await two.MetadataForAsync(again!, Ct)).CurrentVersion.Should().Be(first);
    }

    [Fact]
    public async Task A_module_that_is_not_registered_gives_no_floor()
    {
        var type = NewName();
        using (var seeding = await HostWithAsync(type, module: "GoneModule"))
        {
        }

        // The record names GoneModule, and the host under test does not register it.
        var admin = await factory.CreateClientAsync("SuperAdmin", "Admin");
        var edited = Seeded();
        edited.Transitions.RemoveAt(1);

        var response = await PutAsync(admin, type, edited);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task One_tenants_record_does_not_apply_to_another_tenant()
    {
        var type = NewName();
        var slug = await TenantProfileSeed.TenantAsync(factory, Ct);
        var tenantAdmin = await TenantProfileSeed.AdminInAsync(factory, slug, Ct);
        await StoreTypeAsync(type, slug);
        using var host = await HostWithAsync(type);
        var admin = await AdminOnAsync(host, tenantAdmin);
        var edited = Seeded();
        edited.Transitions.RemoveAt(1);

        var response = await PutAsync(admin, type, edited, tenant: slug);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }
}
