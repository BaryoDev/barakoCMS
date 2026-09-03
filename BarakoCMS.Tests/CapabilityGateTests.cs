using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using FastEndpoints;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The capability gate from issue #272: administrative endpoints ask for a capability the caller's
/// roles carry, rather than for a role name baked into C#.
/// </summary>
/// <remarks>
/// The fixture seeds SuperAdmin, Admin, HR and User with no capabilities at all, which is exactly the
/// state an existing deployment upgrades from. So the back-compat theory here runs against a real
/// pre-upgrade database rather than a contrived one.
/// </remarks>
[Collection("Sequential")]
public class CapabilityGateTests
{
    private readonly IntegrationTestFixture _factory;

    public CapabilityGateTests(IntegrationTestFixture factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// The headline of the issue. No code change, no seeded name: a role invented at runtime and
    /// given the capability reaches the endpoint.
    /// </summary>
    [Fact]
    public async Task A_role_created_at_runtime_with_the_capability_reaches_an_administrative_endpoint()
    {
        var (client, _) = await CallerHolding("Auditor", barakoCMS.Models.SystemCapabilities.ManageRoles);

        var response = await client.GetAsync("/api/roles", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// The other half of #272, and the #166 defect in general form. "Editor" is a name, not a grant.
    /// </summary>
    [Fact]
    public async Task A_role_gains_nothing_from_its_name_alone()
    {
        var (client, _) = await CallerHolding("Editor");

        var roles = await client.GetAsync("/api/roles", TestContext.Current.CancellationToken);
        var tenants = await client.GetAsync("/api/tenants", TestContext.Current.CancellationToken);

        roles.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        tenants.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// A capability names one surface. Holding manage_tenants must not open the roles endpoints,
    /// otherwise the gate is a login check wearing a capability's name.
    /// </summary>
    [Fact]
    public async Task A_capability_opens_only_the_surface_it_names()
    {
        var (client, _) = await CallerHolding("Tenant Operator", barakoCMS.Models.SystemCapabilities.ManageTenants);

        var tenants = await client.GetAsync("/api/tenants", TestContext.Current.CancellationToken);
        var roles = await client.GetAsync("/api/roles", TestContext.Current.CancellationToken);

        tenants.StatusCode.Should().Be(HttpStatusCode.OK);
        roles.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Why this is a per-request lookup and not a claim in the token. The same token, unchanged and
    /// unexpired, stops working the moment the capability is taken off the role.
    /// </summary>
    [Fact]
    public async Task Revoking_a_capability_takes_effect_without_reissuing_the_token()
    {
        var (client, roleId) = await CallerHolding("Revocable Auditor", barakoCMS.Models.SystemCapabilities.ManageRoles);

        var before = await client.GetAsync("/api/roles", TestContext.Current.CancellationToken);
        before.StatusCode.Should().Be(HttpStatusCode.OK);

        var admin = await SuperAdminClient();
        var revoke = await admin.PutAsJsonAsync(
            $"/api/roles/{roleId}",
            new { id = roleId, name = "Revocable Auditor", description = "", permissions = Array.Empty<object>(), systemCapabilities = Array.Empty<string>() },
            TestContext.Current.CancellationToken);
        revoke.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await client.GetAsync("/api/roles", TestContext.Current.CancellationToken);

        after.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Back compatibility, isolated. This caller's stored user holds no roles at all, so the
    /// capability lookup cannot possibly grant anything; only the role name in the token can. That is
    /// the upgraded deployment whose SuperAdmin document has no capabilities yet.
    /// </summary>
    [Theory]
    [InlineData("SuperAdmin", "/api/roles")]
    [InlineData("SuperAdmin", "/api/tenants")]
    [InlineData("Admin", "/api/tenants/members")]
    public async Task A_seeded_role_name_still_opens_the_gate_it_used_to_open(string roleName, string path)
    {
        var client = await CallerWithNoStoredRoles(roleName);

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            "upgrading must not lock out a deployment whose stored roles predate capabilities");
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Admin was never in the <c>Roles("SuperAdmin")</c> gate on roles and tenants, and must not
    /// acquire it here. Without this the back-compat fallback above could be written as "any admin
    /// role opens any admin endpoint" and still look green.
    /// </summary>
    [Fact]
    public async Task The_legacy_fallback_only_honours_the_names_the_endpoint_itself_gated_on()
    {
        var client = await CallerWithNoStoredRoles("Admin");

        var response = await client.GetAsync("/api/roles", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private const string NotAGuid = "not-a-guid";

    /// <summary>
    /// The users routes that were <c>Roles("SuperAdmin")</c> before issue #443 migrated them, and so
    /// are <c>manage_users</c> now. Admin never reached these.
    /// </summary>
    /// <remarks>
    /// Probes use an unparseable id for the same reason <see cref="RoleGateTests"/> does: an allowed
    /// caller is answered 400 by request binding rather than by the handler, which proves the gate
    /// let it through without creating, deleting or resetting anything.
    /// </remarks>
    public static TheoryData<string, string> SuperAdminOnlyUsersRoutes() => new()
    {
        { "GET", "/api/users" },
        { "POST", $"/api/users/{NotAGuid}/password" },
    };

    /// <summary>
    /// The users and user-groups routes that were <c>Roles("SuperAdmin", "Admin")</c>, now
    /// <c>manage_user_membership</c> (the four under <c>/api/users</c>) and
    /// <c>manage_user_groups</c> (the seven under <c>/api/user-groups</c>).
    /// </summary>
    public static TheoryData<string, string> AdminReachableUsersRoutes() => new()
    {
        { "POST", $"/api/users/{NotAGuid}/roles" },
        { "DELETE", $"/api/users/{NotAGuid}/roles/{NotAGuid}" },
        { "POST", $"/api/users/{NotAGuid}/groups" },
        { "DELETE", $"/api/users/{NotAGuid}/groups/{NotAGuid}" },
        { "GET", "/api/user-groups" },
        { "POST", "/api/user-groups" },
        { "GET", $"/api/user-groups/{NotAGuid}" },
        { "PUT", $"/api/user-groups/{NotAGuid}" },
        { "DELETE", $"/api/user-groups/{NotAGuid}" },
        { "POST", $"/api/user-groups/{NotAGuid}/users" },
        { "DELETE", $"/api/user-groups/{NotAGuid}/users/{NotAGuid}" },
    };

    [Fact]
    public async Task A_role_created_at_runtime_with_manage_users_lists_the_accounts()
    {
        var (client, _) = await CallerHolding("Account Auditor", barakoCMS.Models.SystemCapabilities.ManageUsers);

        var response = await client.GetAsync("/api/users", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// The split issue #443 warned about. Assigning roles and groups was Admin-reachable while the
    /// user list and the password reset were SuperAdmin only, so they are two capabilities, and
    /// holding the wide one must not open the narrow one.
    /// </summary>
    [Fact]
    public async Task Manage_user_membership_does_not_open_the_user_list_or_the_password_reset()
    {
        var (client, _) = await CallerHolding("Membership Clerk", barakoCMS.Models.SystemCapabilities.ManageUserMembership);

        var assign = await client.SendAsync(Probe("POST", $"/api/users/{NotAGuid}/roles"), TestContext.Current.CancellationToken);
        var list = await client.GetAsync("/api/users", TestContext.Current.CancellationToken);
        var reset = await client.SendAsync(Probe("POST", $"/api/users/{NotAGuid}/password"), TestContext.Current.CancellationToken);

        assign.StatusCode.Should().NotBe(HttpStatusCode.Forbidden, "manage_user_membership is what this route asks for");
        assign.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
        list.StatusCode.Should().Be(HttpStatusCode.Forbidden, "listing accounts was SuperAdmin only and is manage_users, a separate capability");
        reset.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Manage_user_groups_opens_the_groups_surface_and_nothing_else()
    {
        var (client, _) = await CallerHolding("Group Steward", barakoCMS.Models.SystemCapabilities.ManageUserGroups);

        var groups = await client.GetAsync("/api/user-groups", TestContext.Current.CancellationToken);
        var users = await client.GetAsync("/api/users", TestContext.Current.CancellationToken);
        var assign = await client.SendAsync(Probe("POST", $"/api/users/{NotAGuid}/roles"), TestContext.Current.CancellationToken);

        groups.StatusCode.Should().Be(HttpStatusCode.OK);
        users.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        assign.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Manage_api_keys_opens_the_api_key_surface_and_nothing_else()
    {
        var (client, _) = await CallerHolding("Key Steward", barakoCMS.Models.SystemCapabilities.ManageApiKeys);

        var keys = await client.GetAsync("/api/api-keys", TestContext.Current.CancellationToken);
        var audit = await client.GetAsync("/api/audit", TestContext.Current.CancellationToken);
        var users = await client.GetAsync("/api/users", TestContext.Current.CancellationToken);

        keys.StatusCode.Should().Be(HttpStatusCode.OK);
        audit.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the audit log is view_audit_log, a separate capability, even though both were the same role pair");
        users.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The audit log is its own capability, and it is read-only.
    /// </summary>
    /// <remarks>
    /// Both areas gated on the same <c>Roles("SuperAdmin", "Admin")</c> pair, so one name would have
    /// covered them and the seeded roles would not have noticed. They are split because a runtime
    /// role that should read the audit trail without being able to mint credentials is the ordinary
    /// case for an auditor, and one name makes that unexpressible.
    /// </remarks>
    [Fact]
    public async Task View_audit_log_opens_the_audit_log_and_not_the_keys()
    {
        var (client, _) = await CallerHolding("Compliance Reader", barakoCMS.Models.SystemCapabilities.ViewAuditLog);

        var audit = await client.GetAsync("/api/audit", TestContext.Current.CancellationToken);
        var keys = await client.GetAsync("/api/api-keys", TestContext.Current.CancellationToken);
        var mint = await client.SendAsync(Probe("POST", "/api/api-keys"), TestContext.Current.CancellationToken);

        audit.StatusCode.Should().Be(HttpStatusCode.OK);
        keys.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "an auditor reads the trail; issuing credentials is a different grant");
        mint.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Settings splits into two capabilities, and this is the half an Admin already had.
    /// </summary>
    /// <remarks>
    /// It stops at the email write. That gate was <c>Roles("SuperAdmin")</c> where the rest of
    /// settings was <c>Roles("SuperAdmin", "Admin")</c>, so a single <c>manage_settings</c> would
    /// have handed every Admin the ability to change where the deployment's mail comes from, which
    /// redirects every password reset and every verification token in it.
    /// </remarks>
    [Fact]
    public async Task Manage_settings_opens_the_settings_list_and_stops_at_the_email_write()
    {
        var (client, _) = await CallerHolding("Settings Clerk", barakoCMS.Models.SystemCapabilities.ManageSettings);

        var settings = await client.GetAsync("/api/settings", TestContext.Current.CancellationToken);
        var emailSummary = await client.GetAsync("/api/settings/email", TestContext.Current.CancellationToken);
        var emailWrite = await client.SendAsync(Probe("PUT", "/api/settings/email"), TestContext.Current.CancellationToken);
        var emailTest = await client.SendAsync(Probe("POST", "/api/settings/email/test"), TestContext.Current.CancellationToken);

        settings.StatusCode.Should().Be(HttpStatusCode.OK);
        emailSummary.StatusCode.Should().Be(HttpStatusCode.OK,
            "the summary reports whether a key is set, not what it is, and Admin already read it");
        emailWrite.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "changing the sending identity is manage_email_settings, which this role does not hold");
        emailTest.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "and so is sending real mail through the configured provider");
    }

    /// <summary>
    /// The other half, which no seeded role but SuperAdmin holds.
    /// </summary>
    [Fact]
    public async Task Manage_email_settings_opens_the_email_write_and_not_the_rest_of_settings()
    {
        var (client, _) = await CallerHolding(
            "Mail Operator", barakoCMS.Models.SystemCapabilities.ManageEmailSettings);

        var emailWrite = await client.SendAsync(Probe("PUT", "/api/settings/email"), TestContext.Current.CancellationToken);
        var settings = await client.GetAsync("/api/settings", TestContext.Current.CancellationToken);

        emailWrite.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            "the capability is what this endpoint asks for, so the gate is passed and the body is the "
          + "only thing left to argue about");
        settings.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the two are separate grants in both directions, or splitting them bought nothing");
    }

    /// <summary>
    /// Admin's defaults include manage_settings and deliberately not manage_email_settings.
    /// </summary>
    /// <remarks>
    /// Asserted through the gate rather than through the constant, because the constant agreeing
    /// with itself is not evidence. This is the grant a compromised Admin account does not get.
    /// </remarks>
    [Fact]
    public async Task Admin_defaults_reach_settings_but_not_the_email_write()
    {
        var client = await AdminDefaultsClient();

        var settings = await client.GetAsync("/api/settings", TestContext.Current.CancellationToken);
        var emailWrite = await client.SendAsync(Probe("PUT", "/api/settings/email"), TestContext.Current.CancellationToken);

        settings.StatusCode.Should().Be(HttpStatusCode.OK, "Admin could already read the settings list");
        emailWrite.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "Admin was never in that gate, and the migration does not widen it");
    }

    /// <summary>
    /// Designing a schema and deciding what an anonymous caller can read are two grants.
    /// </summary>
    /// <remarks>
    /// Both were <c>Roles("SuperAdmin", "Admin")</c>, so one name would have covered them and no
    /// seeded role would have noticed. Split for the reason API keys and the audit log are: a role
    /// that models content without also choosing what leaves the building is an ordinary thing to
    /// want, and one name makes it unexpressible.
    /// </remarks>
    [Fact]
    public async Task Manage_content_types_stops_at_the_disclosure_decisions()
    {
        var (client, _) = await CallerHolding(
            "Schema Author", barakoCMS.Models.SystemCapabilities.ManageContentTypes);

        var list = await client.GetAsync("/api/content-types", TestContext.Current.CancellationToken);
        var publicDelivery = await client.SendAsync(
            Probe("PUT", "/api/content-types/nosuchtype/public-delivery"), TestContext.Current.CancellationToken);
        var sensitivity = await client.SendAsync(
            Probe("PUT", "/api/content-types/nosuchtype/fields/Title/sensitivity"), TestContext.Current.CancellationToken);

        list.StatusCode.Should().Be(HttpStatusCode.OK);
        publicDelivery.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "turning anonymous delivery on is manage_public_delivery");
        sensitivity.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "and so is deciding which fields are scrubbed on the way out");
    }

    /// <summary>The other half, and it does not carry the schema surface with it.</summary>
    [Fact]
    public async Task Manage_public_delivery_opens_the_disclosure_decisions_and_not_the_schema()
    {
        var (client, _) = await CallerHolding(
            "Disclosure Officer", barakoCMS.Models.SystemCapabilities.ManagePublicDelivery);

        var publicDelivery = await client.SendAsync(
            Probe("PUT", "/api/content-types/nosuchtype/public-delivery"), TestContext.Current.CancellationToken);
        var create = await client.SendAsync(
            Probe("POST", "/api/content-types"), TestContext.Current.CancellationToken);
        var list = await client.GetAsync("/api/content-types", TestContext.Current.CancellationToken);

        publicDelivery.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            "the capability is what the endpoint asks for, so the gate is passed and only the body is "
          + "left to argue about");
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden, "creating a type is a different grant");
        list.StatusCode.Should().Be(HttpStatusCode.Forbidden, "so is reading the schemas");
    }

    /// <summary>
    /// Admin reached all five content-type routes before this migration and still does.
    /// </summary>
    /// <remarks>
    /// The split is about what a runtime role can be given, not about narrowing a seeded one. A
    /// migration that quietly took something away from Admin would be a breaking change with no
    /// signature change to show for it.
    /// </remarks>
    [Fact]
    public async Task Admin_defaults_still_reach_every_content_type_route()
    {
        var client = await AdminDefaultsClient();

        var list = await client.GetAsync("/api/content-types", TestContext.Current.CancellationToken);
        var schemas = await client.GetAsync("/api/schemas", TestContext.Current.CancellationToken);
        var publicDelivery = await client.SendAsync(
            Probe("PUT", "/api/content-types/nosuchtype/public-delivery"), TestContext.Current.CancellationToken);
        var sensitivity = await client.SendAsync(
            Probe("PUT", "/api/content-types/nosuchtype/fields/Title/sensitivity"), TestContext.Current.CancellationToken);
        var rebuild = await client.SendAsync(
            Probe("POST", "/api/content-types/nosuchtype/rebuild"), TestContext.Current.CancellationToken);

        list.StatusCode.Should().Be(HttpStatusCode.OK);
        schemas.StatusCode.Should().Be(HttpStatusCode.OK, "the alias is the same endpoint and the same gate");
        publicDelivery.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        sensitivity.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        rebuild.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// The invariant of issue #443, asserted through the gate rather than through the constants. A
    /// role holding exactly what the seeder gives Admin, under a name the legacy fallback does not
    /// recognise, reaches every users route Admin reached before.
    /// </summary>
    [Theory]
    [MemberData(nameof(AdminReachableUsersRoutes))]
    public async Task Admins_backfilled_defaults_reach_what_Admin_already_reached(string verb, string path)
    {
        var client = await AdminDefaultsClient();

        var response = await client.SendAsync(Probe(verb, path), TestContext.Current.CancellationToken);

        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            "Admin reached this route under Roles(\"SuperAdmin\", \"Admin\") and its defaults must keep that");
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The other half of the same invariant, and the failure #443 names: handing Admin
    /// <c>manage_users</c> would give every Admin the user list, which it never had.
    /// </summary>
    [Theory]
    [MemberData(nameof(SuperAdminOnlyUsersRoutes))]
    public async Task Admins_backfilled_defaults_do_not_reach_the_SuperAdmin_only_users_routes(string verb, string path)
    {
        var client = await AdminDefaultsClient();

        var response = await client.SendAsync(Probe(verb, path), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "Admin was never in the Roles(\"SuperAdmin\") gate on this route and must not acquire it from its defaults");
    }

    /// <summary>
    /// Back compatibility for the migrated users routes, on a caller whose stored user holds no
    /// roles so only the name in the token can open a gate. SuperAdmin was in every one of these
    /// gates; Admin only in the wide ones.
    /// </summary>
    [Theory]
    [MemberData(nameof(SuperAdminOnlyUsersRoutes))]
    [MemberData(nameof(AdminReachableUsersRoutes))]
    public async Task The_SuperAdmin_name_still_opens_every_migrated_users_gate(string verb, string path)
    {
        var client = await LegacyClient("SuperAdmin");

        var response = await client.SendAsync(Probe(verb, path), TestContext.Current.CancellationToken);

        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            "upgrading must not lock out a deployment whose stored roles predate capabilities");
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [MemberData(nameof(AdminReachableUsersRoutes))]
    public async Task The_Admin_name_still_opens_the_users_gates_it_used_to_open(string verb, string path)
    {
        var client = await LegacyClient("Admin");

        var response = await client.SendAsync(Probe(verb, path), TestContext.Current.CancellationToken);

        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            "upgrading must not lock out a deployment whose stored roles predate capabilities");
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [MemberData(nameof(SuperAdminOnlyUsersRoutes))]
    public async Task The_Admin_name_alone_still_does_not_reach_the_SuperAdmin_only_users_routes(string verb, string path)
    {
        var client = await LegacyClient("Admin");

        var response = await client.SendAsync(Probe(verb, path), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// Structural, in the spirit of <see cref="RoleGateTests"/>: a capability a core endpoint asks
    /// for must be one the vocabulary declares, so a typo cannot ship as an endpoint nobody can reach.
    /// </summary>
    [Fact]
    public void Every_capability_a_core_endpoint_requires_is_one_the_vocabulary_declares()
    {
        var core = typeof(Program).Assembly;

        var required = _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .Where(endpoint => endpoint.Metadata.OfType<EndpointDefinition>()
                .FirstOrDefault()?.EndpointType.Assembly == core)
            .Select(endpoint => endpoint.Metadata.GetMetadata<barakoCMS.Infrastructure.Auth.RequiredCapability>())
            .Where(metadata => metadata is not null)
            .Select(metadata => metadata!.Capability)
            .Distinct()
            .OrderBy(capability => capability, StringComparer.Ordinal)
            .ToList();

        required.Should().NotBeEmpty(
            "core endpoints declare capability gates, so reading none means this test stopped looking");
        required.Should().OnlyContain(capability => barakoCMS.Models.SystemCapabilities.IsKnown(capability));
    }

    private static HttpRequestMessage Probe(string verb, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(verb), path);

        if (verb is "POST" or "PUT")
        {
            // Deliberately not valid JSON, as in RoleGateTests: the gate refuses before a binding
            // failure is answered, and an allowed caller then gets a 400 that mutates nothing.
            request.Content = new StringContent("{", Encoding.UTF8, "application/json");
        }

        return request;
    }

    // One caller of each kind for the whole class rather than one per theory row. The database is
    // shared with every other test in the collection, and a few dozen throwaway users would sit at
    // the top of GET /api/users, where other tests read the first row.
    private static readonly Lock SharedCallers = new();
    private static Task<HttpClient>? _adminDefaults;
    private static Task<HttpClient>? _legacySuperAdmin;
    private static Task<HttpClient>? _legacyAdmin;

    /// <summary>
    /// A caller whose one role holds exactly <c>SystemCapabilities.DefaultsFor("Admin")</c> under a
    /// name the legacy fallback does not honour, so what it reaches is decided by the defaults alone.
    /// </summary>
    private Task<HttpClient> AdminDefaultsClient()
    {
        lock (SharedCallers)
        {
            return _adminDefaults ??= AdminDefaultsCallerAsync();
        }
    }

    private async Task<HttpClient> AdminDefaultsCallerAsync()
    {
        var (client, _) = await CallerHolding(
            "Admin Defaults", barakoCMS.Models.SystemCapabilities.DefaultsFor("Admin").ToArray());
        return client;
    }

    private Task<HttpClient> LegacyClient(string tokenRoleName)
    {
        lock (SharedCallers)
        {
            return tokenRoleName == "SuperAdmin"
                ? _legacySuperAdmin ??= CallerWithNoStoredRoles(tokenRoleName)
                : _legacyAdmin ??= CallerWithNoStoredRoles(tokenRoleName);
        }
    }

    /// <summary>
    /// A caller holding the seeded SuperAdmin role for real, so it is served by the capability path
    /// and not by the legacy name fallback. The revocation test uses it to make the change under
    /// test, and would otherwise be asserting two mechanisms at once.
    /// </summary>
    private async Task<HttpClient> SuperAdminClient()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var userId = Guid.NewGuid();
        session.Store(new barakoCMS.Models.User
        {
            Id = userId,
            Username = $"cap-super-{userId}",
            Email = $"cap-super-{userId}@example.com",
            RoleIds = [barakoCMS.Models.SystemRoles.SuperAdminRoleId],
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: ["SuperAdmin"], userId: userId.ToString()));
        return client;
    }

    /// <summary>
    /// A signed-in caller whose one role is created here with the given capabilities. The role name
    /// is made unique per run: the fixture database is shared and role names are unique.
    /// </summary>
    private async Task<(HttpClient Client, Guid RoleId)> CallerHolding(string roleName, params string[] capabilities)
    {
        var unique = $"{roleName} {Guid.NewGuid():N}";

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var role = new barakoCMS.Models.Role
        {
            Id = Guid.NewGuid(),
            Name = unique,
            SystemCapabilities = capabilities.ToList(),
        };
        session.Store(role);

        var userId = Guid.NewGuid();
        session.Store(new barakoCMS.Models.User
        {
            Id = userId,
            Username = $"cap-{userId}",
            Email = $"cap-{userId}@example.com",
            RoleIds = [role.Id],
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: [unique], userId: userId.ToString()));
        return (client, role.Id);
    }

    /// <summary>
    /// A caller whose token carries a role name but whose stored user holds no roles, so the
    /// capability lookup has nothing to resolve and only the legacy name can open a gate.
    /// </summary>
    private async Task<HttpClient> CallerWithNoStoredRoles(string tokenRoleName)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var userId = Guid.NewGuid();
        session.Store(new barakoCMS.Models.User
        {
            Id = userId,
            Username = $"legacy-{userId}",
            Email = $"legacy-{userId}@example.com",
            RoleIds = [],
        });
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", _factory.CreateToken(roles: [tokenRoleName], userId: userId.ToString()));
        return client;
    }
}
