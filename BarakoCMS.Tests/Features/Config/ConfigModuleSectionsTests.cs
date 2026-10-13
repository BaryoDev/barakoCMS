using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Marten;
using barakoCMS.Models;
using static BarakoCMS.Tests.Features.Config.ConfigCallers;

// The last test reads the obsolete Portability types to check they are marked so.
#pragma warning disable CS0618

namespace BarakoCMS.Tests.Features.Config;

/// <summary>
/// Sections a module registers: a form round trips through the Forms module's provider, and a
/// Portability bundle applies as a config document through the Portability module's (#921).
/// </summary>
[Collection("Sequential")]
public class ConfigModuleSectionsTests
{
    private readonly ConfigCallers _callers;

    public ConfigModuleSectionsTests(IntegrationTestFixture fixture) => _callers = new ConfigCallers(fixture);

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():n}"[..(prefix.Length + 10)];

    private static ContentTypeDefinition Enquiry(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        DisplayName = name,
        Fields =
        [
            new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string", IsRequired = true },
            new FieldDefinition { Name = "Email", DisplayName = "Email", Type = "email" },
        ],
    };

    [Fact]
    public async Task A_form_round_trips_through_the_forms_section()
    {
        var source = await _callers.TenantAsync();
        var target = await _callers.TenantAsync();
        var type = Unique("enquiry");
        await _callers.StoreInTenantAsync(source, Enquiry(type),
            new BarakoCMS.Forms.PublicForm { ContentType = type, EnabledAt = DateTimeOffset.UtcNow, VerifyEmailField = "Email" });

        var (_, document) = await ExportAsync(await _callers.SuperAdminAsync(source), "contentTypes,forms");
        document.GetProperty("forms").GetProperty(type).GetProperty("verifyEmailField").GetString().Should().Be("Email");

        var targetClient = await _callers.SuperAdminAsync(target);
        var (applied, plan) = await ApplyAsync(targetClient, document);
        applied.StatusCode.Should().Be(HttpStatusCode.OK, plan.ToString());
        Changes(plan).Should().Contain($"forms/{type} create");

        var form = await _callers.InTenantAsync(target, s => s.LoadAsync<BarakoCMS.Forms.PublicForm>(type, Ct));
        form.Should().NotBeNull("the forms section created the form in the target tenant");
        form!.VerifyEmailField.Should().Be("Email");
        (await PlanAsync(targetClient, document)).Body.GetProperty("isEmpty").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_form_the_route_would_refuse_is_refused_and_one_left_out_is_destructive()
    {
        var tenant = await _callers.TenantAsync();
        var type = Unique("signup");
        var single = Enquiry(Unique("single"));
        single.IsSingleton = true;
        await _callers.StoreInTenantAsync(tenant, Enquiry(type), single,
            new BarakoCMS.Forms.PublicForm { ContentType = type, EnabledAt = DateTimeOffset.UtcNow });
        var client = await _callers.SuperAdminAsync(tenant);

        var (refused, body) = await ApplyAsync(client, new { version = 1, forms = new Dictionary<string, object> { [single.Name] = new { } } });
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, body.ToString());
        body.ToString().Should().Contain("single entry");

        var (_, plan) = await PlanAsync(client, new { version = 1, forms = new Dictionary<string, object>() });
        Changes(plan).Should().Contain($"forms/{type} destructive");
    }

    [Fact]
    public async Task A_portability_bundle_applies_as_a_config_document()
    {
        var source = await _callers.TenantAsync();
        var target = await _callers.TenantAsync();
        var type = Unique("member");
        await _callers.StoreInTenantAsync(source, Type(type, ("Title", "string")));
        var sourceClient = await _callers.SuperAdminAsync(source);
        foreach (var title in new[] { "Ana", "Ben" })
        {
            (await sourceClient.PostAsJsonAsync("/api/contents", new { contentType = type, status = "Published", data = new Dictionary<string, object> { ["Title"] = title } }, Ct))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var bundleResponse = await sourceClient.GetAsync($"/api/portability/export?types={type}", Ct);
        bundleResponse.StatusCode.Should().Be(HttpStatusCode.OK, "the old route keeps working while it is obsolete");
        var bundle = JsonDocument.Parse(await bundleResponse.Content.ReadAsStringAsync(Ct)).RootElement;
        bundle.GetProperty("contents").GetArrayLength().Should().Be(2);

        var targetClient = await _callers.SuperAdminAsync(target);
        var (planned, plan) = await PlanAsync(targetClient, bundle);
        planned.StatusCode.Should().Be(HttpStatusCode.OK, plan.ToString());
        Changes(plan).Should().Contain($"contentTypes/{type} create");
        Changes(plan).Count(c => c.StartsWith("contents/", StringComparison.Ordinal)).Should().Be(2, plan.ToString());

        var (applied, result) = await ApplyAsync(targetClient, bundle);
        applied.StatusCode.Should().Be(HttpStatusCode.OK, result.ToString());
        var titles = await _callers.InTenantAsync(target, s => s.Query<Content>().Where(c => c.ContentType == type).ToListAsync(Ct));
        titles!.Select(c => c.Data["Title"]?.ToString()).Should().BeEquivalentTo("Ana", "Ben");
    }

    [Fact]
    public async Task Entries_are_exported_only_when_the_contents_section_is_named()
    {
        var tenant = await _callers.TenantAsync();
        var type = Unique("note");
        await _callers.StoreInTenantAsync(tenant, Type(type, ("Title", "string")));
        var client = await _callers.SuperAdminAsync(tenant);
        (await client.PostAsJsonAsync("/api/contents", new { contentType = type, status = "Published", data = new Dictionary<string, object> { ["Title"] = "kept" } }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var (_, everything) = await ExportAsync(client);
        everything.TryGetProperty("contentTypes", out _).Should().BeTrue("the control: the default export carries configuration");
        everything.TryGetProperty("contents", out _).Should().BeFalse("entries are content, not configuration");

        var (_, named) = await ExportAsync(client, "contents");
        named.GetProperty("contents").EnumerateObject().Select(p => p.Value.GetProperty("contentType").GetString()).Should().Contain(type);
    }

    [Fact]
    public void The_portability_routes_and_bundle_types_are_obsolete_and_point_at_the_config_engine()
    {
        Type[] obsolete =
        [
            typeof(BarakoCMS.Portability.ImportEndpoint), typeof(BarakoCMS.Portability.ExportEndpoint),
            typeof(BarakoCMS.Portability.PortabilityBundle), typeof(BarakoCMS.Portability.ContentRecord),
            typeof(BarakoCMS.Portability.ImportRequest), typeof(BarakoCMS.Portability.ImportReport),
        ];

        obsolete.Should().HaveCount(6);
        foreach (var type in obsolete)
        {
            var attribute = type.GetCustomAttribute<ObsoleteAttribute>();
            attribute.Should().NotBeNull($"{type.Name} is retired in favour of the config engine");
            attribute!.Message.Should().Contain("/api/config/apply").And.Contain("6.0");
            attribute.IsError.Should().BeFalse("it keeps working until then");
        }
    }
}
