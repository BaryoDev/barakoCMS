using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;
using BarakoCMS.Tests.Features.Config;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// A type that a role reads only in part is not publicly deliverable, whichever way the flag or the
/// role rule is written: content type create, blueprint apply, role create and update, and the
/// config document. The toggle has its own tests in PublicDeliveryAuditTests.
/// </summary>
[Collection("Sequential")]
public class PublicDeliveryAudienceTests
{
    private readonly IntegrationTestFixture _factory;
    private readonly ConfigCallers _callers;

    public PublicDeliveryAudienceTests(IntegrationTestFixture factory)
    {
        _factory = factory;
        _callers = new ConfigCallers(factory);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():n}"[..(prefix.Length + 10)];

    private static Dictionary<string, object> RowLimit() => new()
    {
        ["Title"] = new Dictionary<string, object> { ["_eq"] = "x" },
    };

    private static Role RoleReading(string type, Dictionary<string, object>? conditions, string? tenant = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = Unique("Aud "),
        TenantSlug = tenant,
        Permissions =
        [
            new ContentTypePermission
            {
                ContentTypeSlug = type,
                Read = new PermissionRule { Enabled = true, Conditions = conditions },
            },
        ],
    };

    private async Task<HttpClient> AdminAsync()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _factory.StoredUserTokenAsync("SuperAdmin"));
        return client;
    }

    private async Task StoreAsync(params object[] documents)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        foreach (var document in documents)
        {
            session.Store(document);
        }

        await session.SaveChangesAsync(Ct);
    }

    private async Task<bool> DeliverableAsync(string type)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return (await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(t => t.Name == type, Ct))?.IsPubliclyDeliverable ?? false;
    }

    private static object NewType(string name, bool deliverable) => new
    {
        name,
        displayName = name,
        isPubliclyDeliverable = deliverable,
        fields = new[] { new { name = "Title", displayName = "Title", type = "string" } },
    };

    [Fact]
    public async Task Creating_a_public_type_is_refused_when_a_role_already_reads_only_part_of_it()
    {
        var type = Unique("cpd");
        await StoreAsync(RoleReading(type, RowLimit()));
        var client = await AdminAsync();

        var refused = await client.PostAsJsonAsync("/api/content-types", NewType(type, deliverable: true), Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, await refused.Content.ReadAsStringAsync(Ct));
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("publicly");
        (await DeliverableAsync(type)).Should().BeFalse();

        var control = await client.PostAsJsonAsync("/api/content-types", NewType(type, deliverable: false), Ct);
        control.StatusCode.Should().Be(HttpStatusCode.OK, "the same type created without public delivery is fine");
    }

    private const string Blueprint = """
        {
          "name": "__NAME__",
          "description": "One public type.",
          "contentTypes": [
            {
              "name": "__TYPE__",
              "displayName": "Case study",
              "isPubliclyDeliverable": __PUBLIC__,
              "fields": [ { "name": "Title", "displayName": "Title", "type": "string", "isRequired": true } ]
            }
          ]
        }
        """;

    [Theory]
    [InlineData(true, HttpStatusCode.BadRequest)]
    [InlineData(false, HttpStatusCode.OK)]
    public async Task Applying_a_blueprint_with_a_public_type_a_role_reads_only_part_of_is_refused(bool limited, HttpStatusCode expected)
    {
        var type = Unique("bpd");
        var name = Unique("bpn");
        if (limited)
        {
            await StoreAsync(RoleReading(type, RowLimit()));
        }

        var dir = Path.Combine(Path.GetTempPath(), $"barako-blueprints-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, $"{name}.json"),
                Blueprint.Replace("__NAME__", name).Replace("__TYPE__", type).Replace("__PUBLIC__", "true"), Ct);
            var host = _factory.WithSetting("Blueprints:Path", dir);
            var client = host.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Bearer", await _factory.StoredUserTokenAsync("SuperAdmin"));

            var response = await client.PostAsync($"/api/content-types/blueprints/{name}", null, Ct);

            response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync(Ct));
            (await DeliverableAsync(type)).Should().Be(!limited);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static object RoleBody(string name, string type, Dictionary<string, object>? conditions) => new
    {
        name,
        description = "audience",
        permissions = new[]
        {
            new
            {
                contentTypeSlug = type,
                read = new { enabled = true, conditions },
            },
        },
        systemCapabilities = Array.Empty<string>(),
    };

    private async Task<string> DeliverableTypeAsync()
    {
        var type = Unique("rpd");
        await StoreAsync(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = type, DisplayName = type, IsPubliclyDeliverable = true,
            Fields = [new FieldDefinition { Name = "Title", DisplayName = "Title", Type = "string" }],
        });
        return type;
    }

    [Fact]
    public async Task Creating_a_role_that_reads_only_part_of_a_public_type_is_refused()
    {
        var type = await DeliverableTypeAsync();
        var client = await AdminAsync();

        var refused = await client.PostAsJsonAsync("/api/roles", RoleBody(Unique("Aud "), type, RowLimit()), Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, await refused.Content.ReadAsStringAsync(Ct));
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("publicly deliverable").And.Contain(type);

        var whole = await client.PostAsJsonAsync("/api/roles", RoleBody(Unique("Aud "), type, null), Ct);
        whole.StatusCode.Should().Be(HttpStatusCode.OK, "a role that reads every row of the type does not limit it");
    }

    [Fact]
    public async Task Saving_a_role_so_it_reads_only_part_of_a_public_type_is_refused()
    {
        var type = await DeliverableTypeAsync();
        var plain = RoleReading(type, null);
        await StoreAsync(plain);
        var client = await AdminAsync();

        var refused = await client.PutAsJsonAsync($"/api/roles/{plain.Id}", RoleBody(plain.Name, type, RowLimit()), Ct);

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest, await refused.Content.ReadAsStringAsync(Ct));
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("publicly deliverable");

        var rename = await client.PutAsJsonAsync($"/api/roles/{plain.Id}", RoleBody(plain.Name, type, null), Ct);
        rename.StatusCode.Should().Be(HttpStatusCode.OK, "the same save without a row limit goes through");
    }

    [Fact]
    public async Task Saving_a_role_that_already_limited_a_public_type_is_not_blocked()
    {
        var type = await DeliverableTypeAsync();
        var held = RoleReading(type, RowLimit());
        await StoreAsync(held);

        var response = await (await AdminAsync()).PutAsJsonAsync($"/api/roles/{held.Id}", RoleBody(held.Name, type, RowLimit()), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "only a new row limit is refused, or the role could not be edited at all");
    }

    [Fact]
    public async Task A_config_document_that_turns_on_delivery_beside_a_role_that_limits_rows_is_refused()
    {
        var tenant = await _callers.TenantAsync();
        var type = Unique("cfd");
        await _callers.StoreInTenantAsync(tenant, ConfigCallers.Type(type, ("Title", "string")));
        await _callers.StoreInTenantAsync(null, RoleReading(type, RowLimit(), tenant));
        var deliverable = ConfigCallers.Type(type, ("Title", "string"));
        deliverable.IsPubliclyDeliverable = true;

        var (response, body) = await ConfigCallers.ApplyAsync(await _callers.SuperAdminAsync(tenant), new
        {
            version = 1,
            contentTypes = new Dictionary<string, object> { [type] = deliverable },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body.ToString());
        body.ToString().Should().Contain("publicly deliverable");
        (await _callers.InTenantAsync(tenant, s => s.Query<ContentTypeDefinition>().SingleAsync(t => t.Name == type, Ct)))!
            .IsPubliclyDeliverable.Should().BeFalse("the refused apply rolled back");
    }

    [Fact]
    public async Task A_config_document_may_turn_on_delivery_and_drop_the_row_limit_together()
    {
        var tenant = await _callers.TenantAsync();
        var type = Unique("cfe");
        var role = RoleReading(type, RowLimit(), tenant);
        await _callers.StoreInTenantAsync(tenant, ConfigCallers.Type(type, ("Title", "string")));
        await _callers.StoreInTenantAsync(null, role);
        var deliverable = ConfigCallers.Type(type, ("Title", "string"));
        deliverable.IsPubliclyDeliverable = true;

        var (response, body) = await ConfigCallers.ApplyAsync(await _callers.SuperAdminAsync(tenant), new
        {
            version = 1,
            contentTypes = new Dictionary<string, object> { [type] = deliverable },
            permissionRules = new Dictionary<string, object> { [$"{role.Name}/{type}"] = new { read = new { enabled = true } } },
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK, body.ToString());
        (await _callers.InTenantAsync(tenant, s => s.Query<ContentTypeDefinition>().SingleAsync(t => t.Name == type, Ct)))!
            .IsPubliclyDeliverable.Should().BeTrue("the document leaves no role with a row limit on it");
    }

    [Fact]
    public async Task A_config_document_that_gives_a_row_limit_on_a_public_type_is_refused()
    {
        var tenant = await _callers.TenantAsync();
        var type = Unique("cff");
        var published = ConfigCallers.Type(type, ("Title", "string"));
        published.IsPubliclyDeliverable = true;
        var role = RoleReading(type, null, tenant);
        await _callers.StoreInTenantAsync(tenant, published);
        await _callers.StoreInTenantAsync(null, role);

        var (response, body) = await ConfigCallers.ApplyAsync(await _callers.SuperAdminAsync(tenant), new
        {
            version = 1,
            permissionRules = new Dictionary<string, object>
            {
                [$"{role.Name}/{type}"] = new { read = new { enabled = true, conditions = RowLimit() } },
            },
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body.ToString());
        body.ToString().Should().Contain("publicly deliverable");
    }
}
