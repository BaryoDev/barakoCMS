using System.Security.Claims;
using barakoCMS.Core.Interfaces;
using barakoCMS.Core.Validation;
using barakoCMS.Features.Public;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

[Collection("Sequential")]
public sealed class OwnerHiddenFieldTests : IDisposable
{
    private readonly IServiceScope _scope;

    public OwnerHiddenFieldTests(IntegrationTestFixture fixture) => _scope = fixture.Services.CreateScope();

    public void Dispose() => _scope.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ISensitivityService Service() => new SensitivityService(
        _scope.ServiceProvider.GetRequiredService<IQuerySession>(),
        new ConfigurationBuilder().Build(), new TenantContext());

    /// <summary>
    /// A stored user. "SuperAdmin" gives them the seeded SuperAdmin role, a capability name gives
    /// them a role of a random name holding it, and anything else a role holding nothing. The
    /// token carries the role names given, which decide nothing.
    /// </summary>
    private async Task<(ClaimsPrincipal Principal, Role Role)> CallerAsync(string access, params string[] tokenRoles)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = $"readrule_{Guid.NewGuid():N}",
            SystemCapabilities = SystemCapabilities.IsKnown(access) ? new List<string> { access } : new List<string>(),
        };
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = $"readrule-{Guid.NewGuid():N}",
            Email = $"readrule-{Guid.NewGuid():N}@example.com",
            RoleIds = access == "SuperAdmin"
                ? new List<Guid> { role.Id, SystemRoles.SuperAdminRoleId }
                : new List<Guid> { role.Id },
        };

        var session = _scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(role);
        session.Store(user);
        await session.SaveChangesAsync(Ct);

        var claims = tokenRoles.Select(r => new Claim(ClaimTypes.Role, r))
            .Append(new Claim("UserId", user.Id.ToString()));
        return (new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test")), role);
    }

    private static FieldDefinition Phone => new()
    {
        Name = "Phone", DisplayName = "Phone", Type = "string", ShownWhenListedIn = "ShownFields",
    };

    private static List<FieldDefinition> MemberFields() =>
    [
        new FieldDefinition { Name = "Name", DisplayName = "Name", Type = "string" },
        new FieldDefinition { Name = "Profession", DisplayName = "Profession", Type = "string" },
        Phone,
        new FieldDefinition { Name = "ShownFields", DisplayName = "Shown fields", Type = "array" },
    ];

    private async Task<string> StoreMemberTypeAsync()
    {
        var name = $"ownerhidden_{Guid.NewGuid():N}";
        var session = _scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            DisplayName = name,
            Fields = MemberFields(),
        });
        await session.SaveChangesAsync(Ct);
        return name;
    }

    private static Dictionary<string, object> Entry(params string[] shown) => new()
    {
        ["Name"] = "Bea", ["Profession"] = "Pediatrics", ["Phone"] = "09170001111", ["ShownFields"] = shown.ToList<object>(),
    };

    private async Task<Dictionary<string, object>> ReadAsAsync(ClaimsPrincipal caller, string type, Dictionary<string, object> data)
    {
        await Service().ApplyAsync(type, SensitivityLevel.Public, data, new DefaultHttpContext { User = caller }, Ct);
        return data;
    }

    [Fact]
    public async Task An_unlisted_field_is_removed_for_a_plain_caller()
    {
        var type = await StoreMemberTypeAsync();
        var (other, _) = await CallerAsync("Viewer");

        var data = await ReadAsAsync(other, type, Entry("Profession"));

        data.Should().NotContainKey("Phone");
        data.Should().ContainKey("Name").And.ContainKey("Profession");
    }

    [Fact]
    public async Task A_listed_field_is_shown()
    {
        var type = await StoreMemberTypeAsync();
        var (other, _) = await CallerAsync("Viewer");

        var data = await ReadAsAsync(other, type, Entry("Phone"));

        data["Phone"].Should().Be("09170001111");
    }

    [Fact]
    public async Task A_missing_list_hides_the_field()
    {
        var type = await StoreMemberTypeAsync();
        var (other, _) = await CallerAsync("Viewer");
        var entry = Entry();
        entry.Remove("ShownFields");

        (await ReadAsAsync(other, type, entry)).Should().NotContainKey("Phone");
    }

    [Fact]
    public async Task The_list_matches_the_field_name_ignoring_case()
    {
        var type = await StoreMemberTypeAsync();
        var (other, _) = await CallerAsync("Viewer");

        (await ReadAsAsync(other, type, Entry("phone"))).Should().ContainKey("Phone");
    }

    [Fact]
    public async Task A_list_that_came_back_from_json_is_read_as_a_list()
    {
        var type = await StoreMemberTypeAsync();
        var (other, _) = await CallerAsync("Viewer");
        var entry = Entry();
        entry["ShownFields"] = System.Text.Json.JsonDocument.Parse("""["Phone"]""").RootElement.Clone();

        (await ReadAsAsync(other, type, entry)).Should().ContainKey("Phone");
    }

    [Fact]
    public async Task A_caller_with_view_sensitive_sees_it()
    {
        var type = await StoreMemberTypeAsync();
        var (officer, _) = await CallerAsync(SystemCapabilities.ViewSensitive);

        (await ReadAsAsync(officer, type, Entry())).Should().ContainKey("Phone");
    }

    [Fact]
    public async Task The_seeded_SuperAdmin_sees_it()
    {
        var type = await StoreMemberTypeAsync();
        var (admin, _) = await CallerAsync("SuperAdmin");

        (await ReadAsAsync(admin, type, Entry())).Should().ContainKey("Phone");
    }

    [Fact]
    public async Task A_role_name_in_the_token_opens_nothing()
    {
        var type = await StoreMemberTypeAsync();
        var (faker, _) = await CallerAsync("Viewer", "Treasurer", "SuperAdmin");

        (await ReadAsAsync(faker, type, Entry())).Should().NotContainKey("Phone");
    }

    [Fact]
    public async Task A_field_without_ShownWhenListedIn_is_untouched()
    {
        var type = await StoreMemberTypeAsync();
        var (other, _) = await CallerAsync("Viewer");

        (await ReadAsAsync(other, type, Entry())).Should().ContainKey("Profession");
    }

    [Fact]
    public async Task A_filter_on_an_owner_hidden_field_is_refused_for_a_plain_caller()
    {
        var (other, _) = await CallerAsync("Viewer");
        var type = await StoreMemberTypeAsync();

        (await Service().MayReadFieldAsync(type, Phone, entry: null, other, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task A_filter_on_an_owner_hidden_field_is_allowed_for_view_sensitive()
    {
        var (officer, _) = await CallerAsync(SystemCapabilities.ViewSensitive);
        var type = await StoreMemberTypeAsync();

        (await Service().MayReadFieldAsync(type, Phone, entry: null, officer, Ct)).Should().BeTrue();
    }

    [Fact]
    public void Anonymous_delivery_never_serves_an_owner_hidden_field()
    {
        var definition = new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = "member", DisplayName = "member", IsPubliclyDeliverable = true, Fields = MemberFields(),
        };
        var entry = new Content
        {
            Id = Guid.NewGuid(), ContentType = "member", Status = ContentStatus.Published, Data = Entry("Phone"),
        };

        var projected = PublicDelivery.ToPublic(entry, definition, slugField: null);

        projected.Should().NotBeNull();
        projected!.Data.Should().ContainKey("Name");
        projected.Data.Should().NotContainKey("Phone");
    }

    [Fact]
    public void An_anonymous_filter_on_an_owner_hidden_field_is_refused()
    {
        var definition = new ContentTypeDefinition { Name = "member", Fields = MemberFields() };

        var parsed = DeliveryQuery.Parse(
            [new KeyValuePair<string, string?>("filter[Phone][eq]", "09170001111")], definition);

        parsed.IsValid.Should().BeFalse();
    }
}
