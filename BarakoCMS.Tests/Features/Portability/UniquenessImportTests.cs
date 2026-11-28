using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.Tests.Features.Config;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Portability;

/// <summary>
/// A type's uniqueness rules travel in a config document, an apply holds its entries to them, and
/// neither an apply nor a spreadsheet import writes two entries holding one value.
/// </summary>
/// <remarks>A tenant per test for the config applies, and a type per test for the spreadsheet import.</remarks>
[Collection("Integration1")]
public class UniquenessImportTests
{
    private static int _ipCounter;

    private readonly IntegrationTestFixture _fixture;

    public UniquenessImportTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Rule = "OneHolderPerBadge";

    private async Task<string> TenantAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var slug = $"uni-{Guid.NewGuid():N}"[..14].ToLowerInvariant();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    private async Task<HttpClient> AdminOfAsync(string? tenantSlug)
    {
        var userId = Guid.NewGuid();

        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new User
            {
                Id = userId,
                Username = $"uni-{Guid.NewGuid():n}"[..14],
                Email = $"uni-{Guid.NewGuid():n}@example.com",
                RoleIds = [SystemRoles.SuperAdminRoleId],
            });
            if (tenantSlug is not null)
            {
                session.Store(new Membership
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    TenantSlug = tenantSlug,
                    Status = MembershipStatus.Active,
                    RoleIds = [SystemRoles.SuperAdminRoleId],
                });
            }

            await session.SaveChangesAsync(Ct);
        }

        var client = _fixture.CreateClient();
        var claims = tenantSlug is null ? null : new Dictionary<string, string> { ["tenant"] = tenantSlug };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", _fixture.CreateToken(roles: ["SuperAdmin", "Admin"], userId: userId.ToString(), additionalClaims: claims));
        if (tenantSlug is not null)
            client.DefaultRequestHeaders.Add("X-Tenant", tenantSlug);
        client.DefaultRequestHeaders.Add(
            TestRemoteIpFilter.Header, $"198.51.106.{Interlocked.Increment(ref _ipCounter) % 200 + 20}");
        return client;
    }

    private static string NewType() => $"imp{Guid.NewGuid():n}"[..14];

    private static ContentTypeDefinition Register(string name, List<UniquenessRule>? rules) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        DisplayName = "Register",
        Fields =
        [
            new FieldDefinition { Name = "Badge", DisplayName = "Badge", Type = "string" },
            new FieldDefinition { Name = "Note", DisplayName = "Note", Type = "string" },
        ],
        Uniqueness = rules,
    };

    private static List<UniquenessRule> BadgeRule() => [new UniquenessRule { Name = Rule, Fields = ["Badge"] }];

    private async Task StoreTypeAsync(string? tenantSlug, ContentTypeDefinition type)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = tenantSlug is null ? store.LightweightSession() : store.LightweightSession(tenantSlug);
        session.Store(type);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<ContentTypeDefinition?> StoredAsync(string tenantSlug, string type)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(tenantSlug);
        return await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type, Ct);
    }

    /// <summary>One entry of the document's <c>contents</c> section.</summary>
    private sealed class Entry
    {
        public string ContentType { get; set; } = string.Empty;
        public Dictionary<string, object> Data { get; set; } = new();
        public string? Status { get; set; }
    }

    private static Entry Record(string type, string badge) => new()
    {
        ContentType = type,
        Data = new Dictionary<string, object> { ["Badge"] = badge },
        Status = "Draft",
    };

    private static Task<HttpResponseMessage> ImportAsync(
        HttpClient client, List<ContentTypeDefinition> types, List<Entry> contents) =>
        client.PostAsJsonAsync(
            "/api/config/apply",
            new { document = new { version = 1, contentTypes = types, contents }, allowDestructive = false },
            ApiJson.Options, Ct);

    private static async Task<string> BodyAsync(HttpResponseMessage response) =>
        (await response.Content.ReadAsStringAsync(Ct)).Replace("\\u0027", "'");

    [Fact]
    public async Task An_apply_with_two_records_holding_one_value_is_refused_naming_the_second()
    {
        var tenant = await TenantAsync();
        var client = await AdminOfAsync(tenant);
        var type = NewType();

        var response = await ImportAsync(
            client, [Register(type, BadgeRule())], [Record(type, "X-1"), Record(type, "X-1"), Record(type, "X-2")]);
        var body = await BodyAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("'#1'").And.Contain(Rule);
        body.Should().NotContain("'#0'").And.NotContain("'#2'");
        (await StoredAsync(tenant, type)).Should().BeNull("an apply is all or nothing");
    }

    [Fact]
    public async Task A_record_with_no_type_or_an_unknown_status_is_refused_naming_it()
    {
        var tenant = await TenantAsync();
        var client = await AdminOfAsync(tenant);
        var type = NewType();
        var noType = Record(type, "X-1");
        noType.ContentType = " ";
        var badStatus = Record(type, "X-2");
        badStatus.Status = "Pubished";

        var response = await ImportAsync(
            client, [Register(type, BadgeRule())], [Record(type, "X-0"), noType, badStatus]);
        var body = await BodyAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("'#1'").And.Contain("'#2'");
        body.Should().NotContain("'#0'");
        (await StoredAsync(tenant, type)).Should().BeNull("an apply is all or nothing");
    }

    [Fact]
    public async Task A_blank_status_stays_published_and_a_status_is_read_without_regard_to_case()
    {
        var tenant = await TenantAsync();
        var client = await AdminOfAsync(tenant);
        var type = NewType();
        var blank = Record(type, "S-1");
        blank.Status = null;
        var lower = Record(type, "S-2");
        lower.Status = "draft";

        var response = await ImportAsync(client, [Register(type, BadgeRule())], [blank, lower]);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(response));
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession(tenant);
        var stored = await session.Query<Content>().Where(c => c.ContentType == type).ToListAsync(Ct);
        stored.Should().HaveCount(2);
        stored.Single(c => c.Data["Badge"].ToString() == "S-1").Status.Should().Be(ContentStatus.Published);
        stored.Single(c => c.Data["Badge"].ToString() == "S-2").Status.Should().Be(ContentStatus.Draft);
    }

    [Fact]
    public async Task A_type_exports_its_rules_and_another_tenant_applies_them()
    {
        var source = await TenantAsync();
        var destination = await TenantAsync();
        var type = NewType();
        await StoreTypeAsync(source, Register(type, BadgeRule()));

        var (exported, document) = await ConfigCallers.ExportAsync(await AdminOfAsync(source), "contentTypes");
        exported.StatusCode.Should().Be(HttpStatusCode.OK, document.ToString());
        document.GetProperty("contentTypes").GetProperty(type).GetProperty("uniqueness").GetArrayLength().Should().Be(1);

        var applied = await (await AdminOfAsync(destination)).PostAsJsonAsync(
            "/api/config/apply", new { document, allowDestructive = false }, Ct);
        applied.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(applied));

        var stored = await StoredAsync(destination, type);
        stored!.Uniqueness.Should().HaveCount(1);
        stored.Uniqueness![0].Name.Should().Be(Rule);
        stored.Uniqueness[0].Fields.Should().Equal("Badge");
    }

    [Fact]
    public async Task An_apply_creating_a_type_whose_rule_names_an_undeclared_field_is_refused()
    {
        var tenant = await TenantAsync();
        var type = NewType();
        var bad = Register(type, [new UniquenessRule { Name = "OneHolderPerSeat", Fields = ["Seat"] }]);

        var response = await ImportAsync(await AdminOfAsync(tenant), [bad], []);
        var body = await BodyAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("names the field 'Seat', which the type does not declare");
        (await StoredAsync(tenant, type)).Should().BeNull();
    }

    [Fact]
    public async Task An_apply_that_changes_a_stored_types_rules_is_refused()
    {
        var tenant = await TenantAsync();
        var type = NewType();
        await StoreTypeAsync(tenant, Register(type, BadgeRule()));

        var changed = Register(type, [new UniquenessRule { Name = "OneHolderPerNote", Fields = ["Note"] }]);
        var response = await ImportAsync(await AdminOfAsync(tenant), [changed], []);
        var body = await BodyAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("uniqueness rules differ from the stored ones");
        (await StoredAsync(tenant, type))!.Uniqueness![0].Name.Should().Be(Rule);
    }

    [Fact]
    public async Task An_apply_with_no_rules_keeps_the_stored_ones()
    {
        var tenant = await TenantAsync();
        var type = NewType();
        await StoreTypeAsync(tenant, Register(type, BadgeRule()));

        var response = await ImportAsync(await AdminOfAsync(tenant), [Register(type, null)], []);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await BodyAsync(response));
        var stored = await StoredAsync(tenant, type);
        stored!.Uniqueness.Should().HaveCount(1);
    }

    [Fact]
    public async Task An_apply_leaving_out_a_field_a_stored_rule_compares_is_refused()
    {
        var tenant = await TenantAsync();
        var type = NewType();
        await StoreTypeAsync(tenant, Register(type, BadgeRule()));

        var withoutBadge = Register(type, null);
        withoutBadge.Fields = [new FieldDefinition { Name = "Note", DisplayName = "Note", Type = "string" }];

        var response = await ImportAsync(await AdminOfAsync(tenant), [withoutBadge], []);
        var body = await BodyAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("is compared by the stored uniqueness rule 'OneHolderPerBadge'");
    }

    [Fact]
    public async Task A_spreadsheet_row_holding_a_value_an_earlier_row_took_is_a_row_error()
    {
        var type = NewType();
        await StoreTypeAsync(null, Register(type, BadgeRule()));
        var client = await AdminOfAsync(null);

        var response = await client.PostAsJsonAsync("/api/import/content", new
        {
            contentType = type,
            continueOnError = true,
            records = new[]
            {
                new Dictionary<string, object> { ["Badge"] = "R-1" },
                new Dictionary<string, object> { ["Badge"] = "R-1" },
                new Dictionary<string, object> { ["Badge"] = "R-2" },
            },
        }, Ct);
        var body = await BodyAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("created").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("failed").GetInt32().Should().Be(1);
        var errors = doc.RootElement.GetProperty("errors").EnumerateArray().ToList();
        errors.Should().HaveCount(1);
        errors[0].GetProperty("row").GetInt32().Should().Be(1);
        errors[0].GetProperty("messages")[0].GetString().Should().Contain(Rule);

        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.QuerySession();
        (await session.Query<Content>().CountAsync(c => c.ContentType == type, Ct)).Should().Be(2);
    }
}
