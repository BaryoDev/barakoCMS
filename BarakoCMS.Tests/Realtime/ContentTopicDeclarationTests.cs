using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Features.Config;
using barakoCMS.Infrastructure.Realtime;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using BarakoCMS.Tests.SampleProbe;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Realtime;

/// <summary>
/// A content type can name a realtime topic. The name is checked when the type is saved, and the
/// catalog follows what is stored without a restart.
/// </summary>
/// <remarks>
/// Each test works in a tenant of its own, so a topic name one test takes is free in the next.
/// Types are saved through the config apply, the writer that carries a whole definition.
/// </remarks>
[Collection("Integration1")]
public class ContentTopicDeclarationTests(SampleProbeHost host) : IClassFixture<SampleProbeHost>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ContentTopicCatalog Catalog => host.Services.GetRequiredService<ContentTopicCatalog>();

    private static object TypeDocument(string? topic, bool branchScoped = false) => new
    {
        displayName = "Ticket",
        fields = new[] { new { name = "Title", displayName = "Title", type = "string" } },
        realtimeTopic = topic,
        branchScoped,
    };

    private static Task<HttpResponseMessage> ApplyAsync(HttpClient client, params (string Name, object Document)[] types) =>
        client.PostAsJsonAsync(
            "/api/config/apply",
            new
            {
                document = new
                {
                    version = 1,
                    contentTypes = types.ToDictionary(t => t.Name, t => t.Document),
                },
            },
            Ct);

    private async Task<(string Tenant, HttpClient Admin)> TenantAsync()
    {
        var tenant = await host.CreateTenantAsync(ct: Ct);
        return (tenant, await host.CreateAdminClientAsync(tenant, Ct));
    }

    [Fact]
    public async Task RealtimeTopic_reads_from_type_config_and_survives_create_and_merge()
    {
        var json = """{ "Name": "ticket", "DisplayName": "Ticket", "RealtimeTopic": "desk.tickets" }""";
        var given = JsonSerializer.Deserialize<ContentTypeDefinition>(json, ConfigJson.Options)!;
        given.RealtimeTopic.Should().Be("desk.tickets");

        ContentTypeUpsert.Create(given).RealtimeTopic.Should().Be("desk.tickets");

        var stored = new ContentTypeDefinition { Name = "ticket", DisplayName = "Ticket" };
        ContentTypeUpsert.MergeInto(stored, given);
        stored.RealtimeTopic.Should().Be("desk.tickets");

        var (_, admin) = await TenantAsync();
        (await ApplyAsync(admin, ("ticket", TypeDocument("desk.tickets")))).StatusCode.Should().Be(HttpStatusCode.OK);

        var list = await admin.GetFromJsonAsync<JsonElement>("/api/content-types", Ct);
        list.GetProperty("items").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "ticket")
            .GetProperty("realtimeTopic").GetString().Should().Be("desk.tickets");
    }

    [Fact]
    public async Task A_bad_topic_name_is_400_with_the_text()
    {
        var (_, admin) = await TenantAsync();

        var response = await ApplyAsync(admin, ("ticket", TypeDocument("Queue Tickets")));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("must be lower case words joined by dots or dashes");
    }

    [Fact]
    public async Task The_workflow_runs_topic_name_is_reserved()
    {
        var (_, admin) = await TenantAsync();

        var response = await ApplyAsync(admin, ("ticket", TypeDocument("workflow-runs")));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("'workflow-runs' is reserved");
    }

    [Fact]
    public async Task A_module_topic_name_is_refused()
    {
        var (_, admin) = await TenantAsync();

        var response = await ApplyAsync(admin, ("ticket", TypeDocument("probe.tickets")));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("is used by module");
    }

    [Fact]
    public async Task A_second_type_cannot_take_the_same_topic()
    {
        var (_, admin) = await TenantAsync();
        (await ApplyAsync(admin, ("ticket", TypeDocument("desk.tickets")))).StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await ApplyAsync(admin, ("order", TypeDocument("desk.tickets")));

        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await second.Content.ReadAsStringAsync(Ct)).Should().Contain("already used by content type 'ticket'");

        (await ApplyAsync(admin, ("ticket", TypeDocument("desk.tickets")))).StatusCode
            .Should().Be(HttpStatusCode.OK, "a type may keep the topic it holds");
    }

    [Fact]
    public async Task Two_types_of_one_document_cannot_take_the_same_topic()
    {
        var (_, admin) = await TenantAsync();

        var response = await ApplyAsync(admin, ("ticket", TypeDocument("desk.tickets")), ("order", TypeDocument("desk.tickets")));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("already used by content type");
    }

    [Fact]
    public async Task The_catalog_finds_a_topic_and_follows_a_rename()
    {
        var (tenant, admin) = await TenantAsync();
        (await ApplyAsync(admin, ("ticket", TypeDocument("desk.tickets")))).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Catalog.FindAsync(tenant, "desk.tickets", Ct)).Should().Be(new ContentTopic("desk.tickets", "ticket", false));
        (await Catalog.ForTypeAsync(tenant, "Ticket", Ct))!.Name.Should().Be("desk.tickets");

        (await ApplyAsync(admin, ("ticket", TypeDocument("queue.calls")))).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Catalog.FindAsync(tenant, "desk.tickets", Ct)).Should().BeNull();
        (await Catalog.FindAsync(tenant, "queue.calls", Ct))!.ContentType.Should().Be("ticket");
    }

    [Fact]
    public async Task The_same_topic_name_may_exist_in_two_tenants()
    {
        var (first, firstAdmin) = await TenantAsync();
        var (second, secondAdmin) = await TenantAsync();

        (await ApplyAsync(firstAdmin, ("ticket", TypeDocument("desk.tickets")))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ApplyAsync(secondAdmin, ("order", TypeDocument("desk.tickets")))).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Catalog.FindAsync(first, "desk.tickets", Ct))!.ContentType.Should().Be("ticket");
        (await Catalog.FindAsync(second, "desk.tickets", Ct))!.ContentType.Should().Be("order");
    }

    [Fact]
    public async Task BranchScoped_is_carried_into_the_topic()
    {
        var (tenant, admin) = await TenantAsync();

        (await ApplyAsync(admin, ("ticket", TypeDocument("desk.tickets", branchScoped: true)))).StatusCode
            .Should().Be(HttpStatusCode.OK);

        (await Catalog.FindAsync(tenant, "desk.tickets", Ct))!.BranchScoped.Should().BeTrue();
    }

    [Fact]
    public async Task A_second_config_plan_shows_no_change()
    {
        var (_, admin) = await TenantAsync();
        var document = new { version = 1, contentTypes = new Dictionary<string, object> { ["ticket"] = TypeDocument("desk.tickets") } };
        (await admin.PostAsJsonAsync("/api/config/apply", new { document }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var plan = await admin.PostAsJsonAsync("/api/config/plan", new { document }, Ct);

        plan.StatusCode.Should().Be(HttpStatusCode.OK);
        (await plan.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("isEmpty").GetBoolean().Should().BeTrue();
    }
}
