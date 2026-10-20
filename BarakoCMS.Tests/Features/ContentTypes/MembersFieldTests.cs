using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Events;
using barakoCMS.Features.Public;
using barakoCMS.Models;
using Probe = BarakoCMS.Tests.TokenFieldProbe;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// Members sits between Sensitive and Public: less restricted than Sensitive, so moving from one to
/// the other is a disclosure, and read alike by every verified viewer, so it carries no role list,
/// no mask and is never a token.
/// </summary>
[Collection("Sequential")]
public class MembersFieldTests
{
    private readonly IntegrationTestFixture _factory;

    public MembersFieldTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FieldDefinition Secret(SensitivityLevel level) =>
        new() { Name = "Secret", DisplayName = "Secret", Type = "string", Sensitivity = level };

    private async Task<(string Type, HttpClient Admin)> TypeWithSecretAsync(SensitivityLevel level)
    {
        var type = await Probe.StoreTypeAsync(_factory, "mem", false, Probe.Text("Name"), Secret(level));
        return (type, Probe.ClientFor(_factory, await Probe.AdminAsync(_factory)));
    }

    private static Task<HttpResponseMessage> SetAsync(HttpClient admin, string type, string field, object body) =>
        admin.PutAsJsonAsync($"/api/content-types/{type}/fields/{field}/sensitivity", body, Ct);

    private async Task<SensitivityLevel> StoredLevelAsync(string type, string field = "Secret") =>
        (await Probe.StoredTypeAsync(_factory, type)).Fields.Single(f => f.Name == field).Sensitivity;

    private async Task<List<string>> AuditActionsAsync(string type)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var entries = await session.Query<AuditEvent>()
            .Where(e => e.Action.StartsWith("contenttype.field.sensitivity"))
            .ToListAsync(Ct);
        return entries
            .Where(e => e.Metadata != null && e.Metadata["contentType"].ToString() == type)
            .Select(e => e.Action)
            .ToList();
    }

    [Fact]
    public async Task Moving_from_Sensitive_to_Members_is_a_disclosure_and_needs_the_acknowledgement()
    {
        var (type, admin) = await TypeWithSecretAsync(SensitivityLevel.Sensitive);

        var refused = await SetAsync(admin, type, "Secret", new { sensitivity = "Members" });
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StoredLevelAsync(type)).Should().Be(SensitivityLevel.Sensitive, "a refused request changes nothing");

        var applied = await SetAsync(admin, type, "Secret", new { sensitivity = "Members", acknowledgeDisclosure = true });
        applied.StatusCode.Should().Be(HttpStatusCode.OK, await applied.Content.ReadAsStringAsync(Ct));
        (await StoredLevelAsync(type)).Should().Be(SensitivityLevel.Members);

        var actions = await AuditActionsAsync(type);
        actions.Should().HaveCount(1);
        actions.Single().Should().Be("contenttype.field.sensitivity.lowered");
    }

    [Fact]
    public async Task Moving_from_Members_to_Sensitive_is_a_raise_and_needs_no_acknowledgement()
    {
        var (type, admin) = await TypeWithSecretAsync(SensitivityLevel.Members);

        var applied = await SetAsync(admin, type, "Secret", new { sensitivity = "Sensitive" });

        applied.StatusCode.Should().Be(HttpStatusCode.OK, await applied.Content.ReadAsStringAsync(Ct));
        (await StoredLevelAsync(type)).Should().Be(SensitivityLevel.Sensitive);
        var actions = await AuditActionsAsync(type);
        actions.Should().HaveCount(1);
        actions.Single().Should().Be("contenttype.field.sensitivity.changed");
    }

    [Fact]
    public async Task Moving_from_Members_to_Public_is_a_disclosure_and_needs_the_acknowledgement()
    {
        var (type, admin) = await TypeWithSecretAsync(SensitivityLevel.Members);

        (await SetAsync(admin, type, "Secret", new { sensitivity = "Public" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StoredLevelAsync(type)).Should().Be(SensitivityLevel.Members);
    }

    [Fact]
    public async Task A_Members_target_refuses_a_role_list_and_a_mask()
    {
        var (type, admin) = await TypeWithSecretAsync(SensitivityLevel.Public);

        (await SetAsync(admin, type, "Secret", new { sensitivity = "Members", visibleToRoles = new[] { "HR" } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SetAsync(admin, type, "Secret", new { sensitivity = "Members", mask = "Last4" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StoredLevelAsync(type)).Should().Be(SensitivityLevel.Public);
    }

    [Fact]
    public async Task A_field_added_as_Members_with_a_role_list_is_refused()
    {
        var (type, admin) = await TypeWithSecretAsync(SensitivityLevel.Public);

        var added = await admin.PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "Perk",
            displayName = "Perk",
            type = "string",
            sensitivity = "Members",
            visibleToRoles = new[] { "HR" },
        }, Ct);

        added.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var stored = (await Probe.StoredTypeAsync(_factory, type)).Fields;
        stored.Should().HaveCount(2, "the seeded Name and Secret fields are still there");
        stored.Should().NotContain(f => f.Name == "Perk");
    }

    [Fact]
    public async Task A_token_field_cannot_be_set_to_Members()
    {
        var type = await Probe.StoreTypeAsync(_factory, "memtok", false, Probe.Text("Name"), Probe.Token());
        var admin = Probe.ClientFor(_factory, await Probe.AdminAsync(_factory));

        var res = await SetAsync(admin, type, Probe.Field, new { sensitivity = "Members", acknowledgeDisclosure = true });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StoredLevelAsync(type, Probe.Field)).Should().Be(SensitivityLevel.Hidden);
    }

    [Fact]
    public async Task A_mask_sent_with_an_added_Members_field_is_not_stored()
    {
        var (type, admin) = await TypeWithSecretAsync(SensitivityLevel.Public);

        var added = await admin.PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "Perk",
            displayName = "Perk",
            type = "string",
            sensitivity = "Members",
            mask = "Redact",
        }, Ct);

        added.StatusCode.Should().Be(HttpStatusCode.OK, await added.Content.ReadAsStringAsync(Ct));
        var perk = (await Probe.StoredTypeAsync(_factory, type)).Fields.Single(f => f.Name == "Perk");
        perk.Sensitivity.Should().Be(SensitivityLevel.Members);
        perk.Mask.Should().Be(FieldMask.Default, "AddField has no mask property, so the one sent is not stored");
    }

    [Fact]
    public async Task A_token_field_cannot_be_added_as_Members()
    {
        var (type, admin) = await TypeWithSecretAsync(SensitivityLevel.Public);

        var added = await admin.PostAsJsonAsync($"/api/content-types/{type}/fields", new
        {
            fieldName = "ClaimToken",
            displayName = "Claim token",
            type = "token",
            sensitivity = "Members",
        }, Ct);

        added.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var stored = (await Probe.StoredTypeAsync(_factory, type)).Fields;
        stored.Should().HaveCount(2, "the seeded Name and Secret fields are still there");
        stored.Should().NotContain(f => f.Name == "ClaimToken");
    }

    [Fact]
    public async Task A_type_created_with_a_Members_token_field_is_refused()
    {
        var name = "memcreate" + Guid.NewGuid().ToString("n")[..10];
        var admin = Probe.ClientFor(_factory, await Probe.AdminAsync(_factory));

        var created = await admin.PostAsJsonAsync("/api/content-types", new
        {
            name,
            displayName = "Ticket",
            fields = new object[]
            {
                new { name = "Name", displayName = "Name", type = "string" },
                new { name = "ClaimToken", displayName = "Claim token", type = "token", sensitivity = "Members" },
            },
        }, Ct);

        created.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("visibleToRoles")]
    [InlineData("mask")]
    public async Task A_type_created_with_a_Members_field_carrying_roles_or_a_mask_is_refused(string extra)
    {
        var name = "memcr" + Guid.NewGuid().ToString("n")[..10];
        var admin = Probe.ClientFor(_factory, await Probe.AdminAsync(_factory));
        var perk = extra == "mask"
            ? (object)new { name = "Perk", displayName = "Perk", type = "string", sensitivity = "Members", mask = "Redact" }
            : new { name = "Perk", displayName = "Perk", type = "string", sensitivity = "Members", visibleToRoles = new[] { "HR" } };

        var created = await admin.PostAsJsonAsync("/api/content-types", new
        {
            name,
            displayName = "Club",
            fields = new object[] { new { name = "Name", displayName = "Name", type = "string" }, perk },
        }, Ct);

        created.StatusCode.Should().Be(HttpStatusCode.BadRequest, await created.Content.ReadAsStringAsync(Ct));
        (await created.Content.ReadAsStringAsync(Ct)).Should().Contain("Perk");

        var plain = await admin.PostAsJsonAsync("/api/content-types", new
        {
            name = name + "ok",
            displayName = "Club",
            fields = new object[]
            {
                new { name = "Name", displayName = "Name", type = "string" },
                new { name = "Perk", displayName = "Perk", type = "string", sensitivity = "Members" },
            },
        }, Ct);
        plain.StatusCode.Should().Be(HttpStatusCode.OK, "positive control: a plain Members field is accepted");
    }

    [Fact]
    public void A_stored_Members_field_with_a_role_list_is_not_delivered_to_a_viewer()
    {
        var plain = Secret(SensitivityLevel.Members);
        var restricted = Secret(SensitivityLevel.Members);
        restricted.VisibleToRoles = new List<string> { "HR" };

        PublicDelivery.IsDeliveredField(plain, member: true).Should().BeTrue();
        PublicDelivery.IsDeliveredField(plain, member: false).Should().BeFalse();
        PublicDelivery.IsDeliveredField(restricted, member: true).Should().BeFalse(
            "a role list on a Members field is a restriction a viewer cannot satisfy");
    }
}
