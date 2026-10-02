using System.Security.Claims;
using barakoCMS.Core.Interfaces;
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

/// <summary>
/// The two read rules a list asks before it lets a caller filter: may this caller see the field,
/// and may they see a document at this sensitivity. The scrub asks the same two, so these are
/// also what decides what a response masks.
/// </summary>
/// <remarks>
/// The rules answer from the caller's stored roles, so every caller here is a stored user, and
/// the service is built on the fixture's store. Two things are answered before any role is read,
/// a Public field or document and everything with the mode off, and those are asked of a service
/// built with no store at all: a rule that reached for one would fail on the null.
/// </remarks>
[Collection("Sequential")]
public sealed class SensitivityReadRuleTests : IDisposable
{
    private readonly IServiceScope _scope;

    public SensitivityReadRuleTests(IntegrationTestFixture fixture) => _scope = fixture.Services.CreateScope();

    public void Dispose() => _scope.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IConfiguration Config(string? mode) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Sensitivity:Mode"] = mode })
            .Build();

    private ISensitivityService Service(string? mode = null) =>
        new SensitivityService(_scope.ServiceProvider.GetRequiredService<IQuerySession>(), Config(mode), new TenantContext());

    private static ISensitivityService WithNoStore(string? mode = null) =>
        new SensitivityService(null!, Config(mode), new TenantContext());

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

    private static ClaimsPrincipal NobodyStored() =>
        new(new ClaimsIdentity(new[] { new Claim("UserId", Guid.NewGuid().ToString()) }, authenticationType: "test"));

    private static FieldDefinition Field(SensitivityLevel level, params string[] visibleTo) => new()
    {
        Name = "Salary",
        Type = "number",
        Sensitivity = level,
        VisibleToRoles = visibleTo.ToList(),
    };

    [Theory]
    [InlineData(SensitivityLevel.Public, "Viewer", true)]
    [InlineData(SensitivityLevel.Sensitive, "Viewer", false)]
    [InlineData(SensitivityLevel.Sensitive, SystemCapabilities.ViewSensitive, true)]
    [InlineData(SensitivityLevel.Sensitive, SystemCapabilities.ViewHidden, false)]
    [InlineData(SensitivityLevel.Hidden, SystemCapabilities.ViewSensitive, false)]
    [InlineData(SensitivityLevel.Hidden, SystemCapabilities.ViewHidden, true)]
    [InlineData(SensitivityLevel.Hidden, "SuperAdmin", true)]
    public async Task A_field_is_readable_by_the_capability_its_level_asks_for(SensitivityLevel level, string access, bool expected)
    {
        var (caller, _) = await CallerAsync(access);

        (await Service().MaySeeFieldAsync(Field(level), caller, Ct)).Should().Be(expected);
    }

    [Fact]
    public async Task A_field_that_names_its_roles_is_readable_by_those_and_not_by_the_level_default()
    {
        var (finance, financeRole) = await CallerAsync("Finance");
        var (nurse, _) = await CallerAsync(SystemCapabilities.ViewSensitive);

        var byId = Field(SensitivityLevel.Sensitive, financeRole.Id.ToString());
        (await Service().MaySeeFieldAsync(byId, finance, Ct)).Should().BeTrue();
        (await Service().MaySeeFieldAsync(byId, nurse, Ct)).Should().BeFalse(
            "view_sensitive is the default for Sensitive, and a field that lists its own roles replaces the default");

        var byName = Field(SensitivityLevel.Sensitive, financeRole.Name);
        (await Service().MaySeeFieldAsync(byName, finance, Ct)).Should().BeTrue(
            "an entry that is not an id is a role name, matched against the caller's stored roles");
        (await Service().MaySeeFieldAsync(byName, nurse, Ct)).Should().BeFalse();
    }

    [Theory]
    [InlineData(SensitivityLevel.Public, "Viewer", true)]
    [InlineData(SensitivityLevel.Sensitive, "Viewer", false)]
    [InlineData(SensitivityLevel.Sensitive, SystemCapabilities.ViewSensitive, true)]
    [InlineData(SensitivityLevel.Sensitive, SystemCapabilities.ViewHidden, false)]
    [InlineData(SensitivityLevel.Hidden, SystemCapabilities.ViewSensitive, false)]
    [InlineData(SensitivityLevel.Hidden, SystemCapabilities.ViewHidden, true)]
    [InlineData(SensitivityLevel.Hidden, "SuperAdmin", true)]
    public async Task A_document_is_readable_by_the_capability_its_level_asks_for(SensitivityLevel level, string access, bool expected)
    {
        var (caller, _) = await CallerAsync(access);

        (await Service().MaySeeDocumentAsync(level, caller, Ct)).Should().Be(expected);
    }

    [Fact]
    public async Task A_role_name_in_the_token_opens_nothing()
    {
        var (caller, role) = await CallerAsync("Viewer", "HR", "SuperAdmin");

        (await Service().MaySeeFieldAsync(Field(SensitivityLevel.Sensitive), caller, Ct)).Should().BeFalse(
            "the stored role holds no view_sensitive, whatever the token calls it");
        (await Service().MaySeeFieldAsync(Field(SensitivityLevel.Hidden), caller, Ct)).Should().BeFalse(
            "and it is not the seeded SuperAdmin role");
        (await Service().MaySeeFieldAsync(Field(SensitivityLevel.Sensitive, "HR"), caller, Ct)).Should().BeFalse(
            "a list is matched against the names of the stored roles, not the token's");
        (await Service().MaySeeDocumentAsync(SensitivityLevel.Sensitive, caller, Ct)).Should().BeFalse();
        (await Service().MaySeeDocumentAsync(SensitivityLevel.Hidden, caller, Ct)).Should().BeFalse();

        (await Service().MaySeeFieldAsync(Field(SensitivityLevel.Sensitive, role.Name), caller, Ct)).Should().BeTrue(
            "the control: the caller is found in the store and its own role does open a list that names it");
    }

    [Fact]
    public async Task A_caller_the_store_does_not_know_reads_only_what_is_public()
    {
        var nobody = NobodyStored();

        (await Service().MaySeeFieldAsync(Field(SensitivityLevel.Sensitive), nobody, Ct)).Should().BeFalse();
        (await Service().MaySeeDocumentAsync(SensitivityLevel.Hidden, nobody, Ct)).Should().BeFalse();
        (await Service().MaySeeFieldAsync(Field(SensitivityLevel.Public), nobody, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task A_public_field_or_document_is_answered_without_reading_the_callers_roles()
    {
        var nobody = NobodyStored();

        (await WithNoStore().MaySeeFieldAsync(Field(SensitivityLevel.Public), nobody, Ct)).Should().BeTrue(
            "there is no store behind this service, so an answer means no role was read");
        (await WithNoStore().MaySeeDocumentAsync(SensitivityLevel.Public, nobody, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task With_the_mode_off_every_field_and_document_is_readable()
    {
        var (viewer, _) = await CallerAsync("Viewer");

        (await Service().MaySeeFieldAsync(Field(SensitivityLevel.Hidden), viewer, Ct)).Should().BeFalse(
            "in the default mode a plain caller is refused, or the lines below prove nothing");
        (await Service().MaySeeDocumentAsync(SensitivityLevel.Hidden, viewer, Ct)).Should().BeFalse();

        (await WithNoStore("Off").MaySeeFieldAsync(Field(SensitivityLevel.Hidden), viewer, Ct)).Should().BeTrue(
            "nothing is masked with the mode off, so nothing is withheld from a filter either");
        (await WithNoStore("Off").MaySeeFieldAsync(Field(SensitivityLevel.Sensitive), viewer, Ct)).Should().BeTrue();
        (await WithNoStore("Off").MaySeeDocumentAsync(SensitivityLevel.Hidden, viewer, Ct)).Should().BeTrue();
        (await WithNoStore("Off").MaySeeDocumentAsync(SensitivityLevel.Sensitive, viewer, Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task With_the_mode_off_the_scrub_leaves_a_hidden_document_as_it_is()
    {
        var data = new Dictionary<string, object> { ["Salary"] = 60000 };

        var hidden = await WithNoStore("Off").ApplyAsync(
            "staff", SensitivityLevel.Hidden, data, new DefaultHttpContext { User = NobodyStored() }, Ct);

        hidden.Should().BeFalse();
        data.Should().ContainKey("Salary");
    }

    [Theory]
    [InlineData(SensitivityLevel.Hidden, true)]
    [InlineData(SensitivityLevel.Sensitive, false)]
    public async Task The_scrub_clears_a_document_the_read_rule_refuses(SensitivityLevel level, bool reportedHidden)
    {
        var (viewer, _) = await CallerAsync("Viewer");
        var data = new Dictionary<string, object> { ["Salary"] = 60000 };

        var hidden = await Service().ApplyAsync(
            "staff", level, data, new DefaultHttpContext { User = viewer }, Ct);

        hidden.Should().Be(reportedHidden, "only a Hidden document reports itself as hidden");
        data.Should().BeEmpty();
    }
}
