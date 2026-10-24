using FluentAssertions;
using Marten;
using barakoCMS.Models;
using static BarakoCMS.Tests.Features.Config.ConfigCallers;

namespace BarakoCMS.Tests.Features.Config;

/// <summary>
/// A content type's uniqueness rules in the config document: stored when the type is created, checked
/// the way create checks them, round-tripped by export, and left alone by a document without them.
/// </summary>
[Collection("Integration3")]
public class ConfigUniquenessTests
{
    private readonly ConfigCallers _callers;

    public ConfigUniquenessTests(IntegrationTestFixture fixture) => _callers = new ConfigCallers(fixture);

    private static string Unique(string prefix) => $"{prefix}{Guid.NewGuid():n}"[..(prefix.Length + 10)];

    private static ContentTypeDefinition Badge(string name, params UniquenessRule[] rules)
    {
        var type = Type(name, ("Badge", "string"), ("Note", "string"));
        type.Uniqueness = rules.Length == 0 ? null : rules.ToList();
        return type;
    }

    private static UniquenessRule OneHolder(string field = "Badge") =>
        new() { Name = "OneHolderPerBadge", Fields = [field] };

    private async Task<List<UniquenessRule>?> StoredRulesAsync(string tenant, string name) =>
        (await _callers.InTenantAsync(tenant, s => s.Query<ContentTypeDefinition>().SingleAsync(t => t.Name == name, Ct)))!.Uniqueness;

    [Fact]
    public async Task A_new_type_applied_from_a_document_stores_its_uniqueness_rules()
    {
        var tenant = await _callers.TenantAsync();
        var name = Unique("badge");
        var client = await _callers.SuperAdminAsync(tenant);

        var (response, body) = await ApplyAsync(client, new
        {
            version = 1,
            contentTypes = new Dictionary<string, object> { [name] = Badge(name, OneHolder()) },
        });

        response.IsSuccessStatusCode.Should().BeTrue(body.ToString());
        var rules = await StoredRulesAsync(tenant, name);
        rules.Should().NotBeNull();
        rules.Should().ContainSingle();
        rules![0].Name.Should().Be("OneHolderPerBadge");
        rules[0].Fields.Should().Equal("Badge");
    }

    [Fact]
    public async Task A_rule_create_would_refuse_is_refused_by_apply_and_nothing_is_stored()
    {
        var tenant = await _callers.TenantAsync();
        var name = Unique("badge");
        var client = await _callers.SuperAdminAsync(tenant);

        var (response, body) = await ApplyAsync(client, new
        {
            version = 1,
            contentTypes = new Dictionary<string, object> { [name] = Badge(name, OneHolder("NoSuchField")) },
        });

        response.IsSuccessStatusCode.Should().BeFalse(body.ToString());
        body.ToString().Should().Contain("NoSuchField");
        (await _callers.InTenantAsync(tenant, s => s.Query<ContentTypeDefinition>().AnyAsync(t => t.Name == name, Ct)))
            .Should().BeFalse();
    }

    [Fact]
    public async Task An_export_carries_the_rules_and_applying_it_to_another_tenant_stores_them()
    {
        var source = await _callers.TenantAsync();
        var target = await _callers.TenantAsync();
        var name = Unique("badge");
        await _callers.StoreInTenantAsync(source, Badge(name, OneHolder()));

        var (exported, document) = await ExportAsync(await _callers.SuperAdminAsync(source), "contentTypes");
        exported.IsSuccessStatusCode.Should().BeTrue(document.ToString());
        document.ToString().Should().Contain("OneHolderPerBadge");

        var (applied, plan) = await ApplyAsync(await _callers.SuperAdminAsync(target), document);
        applied.IsSuccessStatusCode.Should().BeTrue(plan.ToString());
        var rules = await StoredRulesAsync(target, name);
        rules.Should().NotBeNull();
        rules.Should().ContainSingle(r => r.Name == "OneHolderPerBadge" && r.Fields.SequenceEqual(new[] { "Badge" }));
    }

    [Fact]
    public async Task A_document_without_uniqueness_keeps_the_rules_a_stored_type_holds()
    {
        var tenant = await _callers.TenantAsync();
        var name = Unique("badge");
        await _callers.StoreInTenantAsync(tenant, Badge(name, OneHolder()));
        var client = await _callers.SuperAdminAsync(tenant);

        var changed = Badge(name);
        changed.DisplayName = "Badges";
        var (response, body) = await ApplyAsync(client, new
        {
            version = 1,
            contentTypes = new Dictionary<string, object> { [name] = changed },
        });

        response.IsSuccessStatusCode.Should().BeTrue(body.ToString());
        (await _callers.InTenantAsync(tenant, s => s.Query<ContentTypeDefinition>().SingleAsync(t => t.Name == name, Ct)))!
            .DisplayName.Should().Be("Badges", "the document did change the type");
        var rules = await StoredRulesAsync(tenant, name);
        rules.Should().NotBeNull();
        rules.Should().ContainSingle(r => r.Name == "OneHolderPerBadge");
    }

    [Fact]
    public async Task A_document_with_different_rules_for_a_stored_type_is_pointed_at_the_uniqueness_route()
    {
        var tenant = await _callers.TenantAsync();
        var name = Unique("badge");
        await _callers.StoreInTenantAsync(tenant, Badge(name, OneHolder()));
        var client = await _callers.SuperAdminAsync(tenant);

        var (response, body) = await ApplyAsync(client, new
        {
            version = 1,
            contentTypes = new Dictionary<string, object> { [name] = Badge(name, new UniquenessRule { Name = "OneNote", Fields = ["Note"] }) },
        });

        response.IsSuccessStatusCode.Should().BeFalse(body.ToString());
        body.ToString().Should().Contain($"PUT /api/content-types/{name}/uniqueness");
        var rules = await StoredRulesAsync(tenant, name);
        rules.Should().NotBeNull();
        rules.Should().ContainSingle(r => r.Name == "OneHolderPerBadge");
    }
}
