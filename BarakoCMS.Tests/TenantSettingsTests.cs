using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BarakoCMS.Email.Smtp;
using BarakoCMS.Files.S3;
using BarakoCMS.Tests.Features.Email;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using barakoCMS.Modules;

namespace BarakoCMS.Tests;

/// <summary>
/// One settings document per tenant, with sections the modules declare (#896, #890).
/// </summary>
/// <remarks>
/// The fixture host runs no modules and so has an empty catalogue. These tests use a derived host
/// that knows the SMTP and S3 modules, built once for the class.
/// </remarks>
[Collection("Integration1")]
public class TenantSettingsTests
{
    private const string SecretValue = "AKIA-tenant-secret-nobody-should-read-back";
    private const string HostFrom = "host-default@example.com";

    private static readonly Lock Gate = new();
    private static WebApplicationFactory<Program>? _withModules;
    private static int _ipCounter;

    private readonly IntegrationTestFixture _factory;

    public TenantSettingsTests(IntegrationTestFixture factory) => _factory = factory;

    private WebApplicationFactory<Program> HostWithModules()
    {
        lock (Gate)
        {
            return _withModules ??= _factory.WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?> { [$"{SmtpOptions.SectionName}:From"] = HostFrom }));
                builder.ConfigureServices(services =>
                {
                    IBarakoModule[] modules = [new SmtpEmailModule(), new S3FilesModule()];
                    services.AddSingleton(ModuleCatalogue.Of(modules, modules));
                });
            });
        }
    }

    private static string NextIp() =>
        $"198.51.102.{Interlocked.Increment(ref _ipCounter) % 250 + 1}";

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20].ToLowerInvariant();

    [Fact]
    public async Task A_tenant_smtp_from_is_used_for_that_tenant_and_another_tenant_keeps_the_host_default()
    {
        var host = HostWithModules();
        var tenantA = await TenantAsync();
        var tenantB = await TenantAsync();
        var admin = await MemberClientAsync(host, tenantA, SystemRoles.AdminRoleId, "Admin");

        var put = await admin.PutAsJsonAsync("/api/tenant-settings/Email.Smtp",
            new { values = new Dictionary<string, string?> { ["From"] = "hello@tenant-a.example" } },
            TestContext.Current.CancellationToken);
        put.StatusCode.Should().Be(HttpStatusCode.OK,
            await put.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        using var relay = new FakeSmtpServer();
        await SendForTenantAsync(host, relay.Port, tenantA, "tenant-a.example");
        await SendForTenantAsync(host, relay.Port, tenantB, "tenant-a.example");

        var messages = relay.Messages;
        messages.Should().HaveCount(2);
        messages[0].Should().Contain("hello@tenant-a.example");
        messages[0].Should().NotContain(HostFrom);
        messages[1].Should().Contain(HostFrom, "tenant B set nothing, so it keeps the host default");
        messages[1].Should().NotContain("tenant-a.example");
    }

    [Fact]
    public async Task A_tenant_smtp_from_on_an_unlisted_domain_sends_from_the_host_sender()
    {
        var host = HostWithModules();
        var tenant = await TenantAsync();
        var admin = await MemberClientAsync(host, tenant, SystemRoles.AdminRoleId, "Admin");
        (await admin.PutAsJsonAsync("/api/tenant-settings/Email.Smtp",
            new { values = new Dictionary<string, string?> { ["From"] = "ceo@bigbank.example" } },
            TestContext.Current.CancellationToken)).IsSuccessStatusCode.Should().BeTrue("PUT does not decide who may send");

        using var relay = new FakeSmtpServer();
        await SendForTenantAsync(host, relay.Port, tenant);
        await SendForTenantAsync(host, relay.Port, tenant, "other.example");
        await SendForTenantAsync(host, relay.Port, tenant, "mail.bigbank.example");

        var messages = relay.Messages;
        messages.Should().HaveCount(3);
        messages.Should().OnlyContain(m => m.Contains(HostFrom) && !m.Contains("bigbank.example"),
            "an empty list, another domain and a parent domain of the listed one all fall back to the host sender");
    }

    [Fact]
    public async Task A_listed_domain_is_matched_exactly_and_without_regard_to_case()
    {
        var host = HostWithModules();
        var tenant = await TenantAsync();
        var admin = await MemberClientAsync(host, tenant, SystemRoles.AdminRoleId, "Admin");
        (await admin.PutAsJsonAsync("/api/tenant-settings/Email.Smtp",
            new { values = new Dictionary<string, string?> { ["From"] = "Hello@Tenant-A.Example" } },
            TestContext.Current.CancellationToken)).IsSuccessStatusCode.Should().BeTrue();

        using var relay = new FakeSmtpServer();
        await SendForTenantAsync(host, relay.Port, tenant, "TENANT-a.example");
        await SendForTenantAsync(host, relay.Port, tenant, "a.example");

        var messages = relay.Messages;
        messages.Should().HaveCount(2);
        messages[0].Should().Contain("Tenant-A.Example").And.NotContain(HostFrom);
        messages[1].Should().Contain(HostFrom, "a.example is not the sender's domain, and there is no subdomain wildcard");
    }

    [Fact]
    public async Task Account_mail_keeps_the_host_sender_even_when_the_tenant_has_an_smtp_from()
    {
        var host = HostWithModules();
        var tenant = await TenantAsync();
        var admin = await MemberClientAsync(host, tenant, SystemRoles.AdminRoleId, "Admin");
        (await admin.PutAsJsonAsync("/api/tenant-settings/Email.Smtp",
            new { values = new Dictionary<string, string?> { ["From"] = "ceo@bigbank.example" } },
            TestContext.Current.CancellationToken)).IsSuccessStatusCode.Should().BeTrue();

        using var relay = new FakeSmtpServer();
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Slug = tenant;
        await Smtp(scope, relay.Port, "bigbank.example").SendEmailAsync("someone@example.com", "Your sign-in code", "<p>1</p>",
            TestContext.Current.CancellationToken);

        var messages = relay.Messages;
        messages.Should().HaveCount(1);
        messages[0].Should().Contain(HostFrom).And.NotContain("bigbank.example", "account mail never takes the tenant sender, even for a listed domain");
    }

    [Fact]
    public async Task A_declared_section_appears_with_its_description()
    {
        var host = HostWithModules();
        var admin = await MemberClientAsync(host, await TenantAsync(), SystemRoles.AdminRoleId, "Admin");

        var res = await admin.GetAsync("/api/tenant-settings/Email.Smtp", TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        json.RootElement.GetProperty("section").GetString().Should().Be("Email.Smtp");
        json.RootElement.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
        var keys = json.RootElement.GetProperty("keys").EnumerateArray().ToArray();
        keys.Should().HaveCount(1, "the SMTP module declares only the sender");
        keys[0].GetProperty("key").GetString().Should().Be("From");
        keys[0].GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
        keys[0].GetProperty("set").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_secret_never_appears_in_any_get_body_and_is_encrypted_at_rest()
    {
        var host = HostWithModules();
        var tenant = await TenantAsync();
        var admin = await MemberClientAsync(host, tenant, SystemRoles.AdminRoleId, "Admin");

        var put = await admin.PutAsJsonAsync("/api/tenant-settings/Files.S3",
            new { values = new Dictionary<string, string?> { ["Bucket"] = "tenant-bucket", ["SecretKey"] = SecretValue } },
            TestContext.Current.CancellationToken);
        var putBody = await put.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        put.StatusCode.Should().Be(HttpStatusCode.OK, putBody);
        putBody.Should().NotContain(SecretValue);

        var read = await admin.GetAsync("/api/tenant-settings/Files.S3", TestContext.Current.CancellationToken);
        var body = await read.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().NotContain(SecretValue);
        using (var json = JsonDocument.Parse(body))
        {
            var keys = json.RootElement.GetProperty("keys").EnumerateArray().ToArray();
            var secret = keys.Single(k => k.GetProperty("key").GetString() == "SecretKey");
            secret.GetProperty("set").GetBoolean().Should().BeTrue();
            secret.GetProperty("value").ValueKind.Should().Be(JsonValueKind.Null);
            keys.Single(k => k.GetProperty("key").GetString() == "Bucket")
                .GetProperty("value").GetString().Should().Be("tenant-bucket");
        }

        // The stored secret does decrypt, so the places that could hand it out are worth checking. The
        // Files.S3 view never overlays tenant values, so also set the Email.Smtp sender, the module
        // whose view does, and read both views as a global admin on this tenant.
        using (var inner = host.Services.CreateScope())
        {
            var settings = inner.ServiceProvider.GetRequiredService<ITenantSettings>();
            (await settings.GetOverrideAsync(tenant, "Files.S3", "SecretKey", TestContext.Current.CancellationToken))
                .Should().Be(SecretValue, "the resolver is the one reader that may see a secret");
        }

        (await admin.PutAsJsonAsync("/api/tenant-settings/Email.Smtp",
            new { values = new Dictionary<string, string?> { ["From"] = "hello@tenant-s.example" } },
            TestContext.Current.CancellationToken)).IsSuccessStatusCode.Should().BeTrue();
        var superAdmin = await MemberClientAsync(host, tenant, SystemRoles.SuperAdminRoleId, "SuperAdmin");
        foreach (var module in new[] { "Files.S3", "Email.Smtp" })
        {
            var config = await superAdmin.GetAsync($"/api/modules/{module}/configuration", TestContext.Current.CancellationToken);
            config.StatusCode.Should().Be(HttpStatusCode.OK);
            (await config.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().NotContain(SecretValue);
        }

        (await FromEntryAsync(superAdmin)).GetProperty("value").GetString().Should().Be("hello@tenant-s.example",
            "the Email.Smtp view does overlay tenant values, so the check above is not a view that never shows them");

        using var scope = host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var row = await session.LoadAsync<TenantSettings>(TenantSettings.IdForTenant(tenant),
            TestContext.Current.CancellationToken);
        row.Should().NotBeNull();
        row!.Values.Values.Should().NotContain(SecretValue);
        row.ProtectedValues.Values.Should().NotBeEmpty().And.NotContain(v => v.Contains(SecretValue));
    }

    [Fact]
    public async Task An_unknown_section_is_404()
    {
        var admin = await MemberClientAsync(HostWithModules(), await TenantAsync(), SystemRoles.AdminRoleId, "Admin");

        (await admin.GetAsync("/api/tenant-settings/No.Such.Section", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.PutAsJsonAsync("/api/tenant-settings/No.Such.Section",
            new { values = new Dictionary<string, string?> { ["From"] = "a@b.example" } },
            TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_unknown_key_is_400_and_nothing_in_the_request_is_stored()
    {
        var host = HostWithModules();
        var tenant = await TenantAsync();
        var admin = await MemberClientAsync(host, tenant, SystemRoles.AdminRoleId, "Admin");

        var res = await admin.PutAsJsonAsync("/api/tenant-settings/Email.Smtp",
            new { values = new Dictionary<string, string?> { ["From"] = "ok@tenant.example", ["Nope"] = "x" } },
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var scope = host.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<IQuerySession>()
            .LoadAsync<TenantSettings>(TenantSettings.IdForTenant(tenant), TestContext.Current.CancellationToken);
        (row?.Values ?? []).Should().BeEmpty("the valid key in the same request must not be half-applied");
    }

    [Fact]
    public async Task A_host_only_key_cannot_be_overridden_by_a_tenant()
    {
        var admin = await MemberClientAsync(HostWithModules(), await TenantAsync(), SystemRoles.AdminRoleId, "Admin");

        var res = await admin.PutAsJsonAsync("/api/tenant-settings/Files.S3",
            new { values = new Dictionary<string, string?> { ["ServiceUrl"] = "https://evil.example" } },
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_member_without_the_settings_capability_is_refused()
    {
        var member = await MemberClientAsync(HostWithModules(), await TenantAsync(), SystemRoles.UserRoleId, "User");

        (await member.GetAsync("/api/tenant-settings/Email.Smtp", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await member.PutAsJsonAsync("/api/tenant-settings/Email.Smtp",
            new { values = new Dictionary<string, string?> { ["From"] = "a@b.example" } },
            TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("Email.Smtp", "From", "not a mailbox")]
    [InlineData("Email.Smtp", "From", "a@b.example\r\nBcc: victim@example.com")]
    [InlineData("Email.Smtp", "From", "a@b.example, c@d.example")]
    [InlineData("Email.Smtp", "From", "a@b.example; c@d.example")]
    [InlineData("Files.S3", "PublicBaseUrl", "http://files.tenant.example/")]
    [InlineData("Files.S3", "PublicBaseUrl", "files.tenant.example")]
    public async Task A_value_of_the_wrong_kind_is_400_names_the_key_and_stores_nothing(string section, string key, string value)
    {
        var host = HostWithModules();
        var tenant = await TenantAsync();
        var admin = await MemberClientAsync(host, tenant, SystemRoles.AdminRoleId, "Admin");

        var res = await admin.PutAsJsonAsync($"/api/tenant-settings/{section}",
            new { values = new Dictionary<string, string?> { [key] = value } },
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain(key).And.NotContain("victim@example.com").And.NotContain("tenant.example");
        using var scope = host.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<ITenantSettings>()
            .GetOverrideAsync(tenant, section, key, TestContext.Current.CancellationToken)).Should().BeNull();
    }

    [Fact]
    public async Task A_https_url_and_a_plain_mailbox_are_accepted()
    {
        var admin = await MemberClientAsync(HostWithModules(), await TenantAsync(), SystemRoles.AdminRoleId, "Admin");

        (await admin.PutAsJsonAsync("/api/tenant-settings/Files.S3",
            new { values = new Dictionary<string, string?> { ["PublicBaseUrl"] = "https://files.tenant.example/" } },
            TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.PutAsJsonAsync("/api/tenant-settings/Email.Smtp",
            new { values = new Dictionary<string, string?> { ["From"] = "Tenant Team <team@tenant.example>" } },
            TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Resolution_prefers_the_tenant_value_then_the_stored_host_default_then_configuration()
    {
        var host = HostWithModules();
        var tenant = await TenantAsync();
        var admin = await MemberClientAsync(host, tenant, SystemRoles.AdminRoleId, "Admin");
        var ct = TestContext.Current.CancellationToken;
        var stored = new SystemSetting { Id = Guid.NewGuid(), Key = "Email.Smtp:From", Value = "stored@example.com" };

        async Task<string?> EffectiveAsync()
        {
            using var scope = host.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ITenantSettings>().GetAsync(tenant, "Email.Smtp", "From", ct);
        }

        await EffectiveAsync().ContinueWith(t => t.Result.Should().Be(HostFrom, "configuration is the last step"), ct);

        await StoreAsync(stored);
        try
        {
            (await EffectiveAsync()).Should().Be("stored@example.com", "the stored host default beats configuration");

            (await admin.PutAsJsonAsync("/api/tenant-settings/Email.Smtp",
                new { values = new Dictionary<string, string?> { ["From"] = "own@tenant.example" } }, ct))
                .IsSuccessStatusCode.Should().BeTrue();
            (await EffectiveAsync()).Should().Be("own@tenant.example", "the tenant value beats both");

            (await admin.PutAsJsonAsync("/api/tenant-settings/Email.Smtp",
                new { values = new Dictionary<string, string?> { ["From"] = "" } }, ct))
                .IsSuccessStatusCode.Should().BeTrue();
            (await EffectiveAsync()).Should().Be("stored@example.com", "clearing hands the key back to the host default");
        }
        finally
        {
            using var scope = _factory.Services.CreateScope();
            var session = scope.ServiceProvider.GetRequiredService<IDocumentStore>().LightweightSession();
            session.Delete<SystemSetting>(stored.Id);
            await session.SaveChangesAsync(CancellationToken.None);
        }

        (await EffectiveAsync()).Should().Be(HostFrom, "with the stored default gone, configuration is back");
    }

    [Fact]
    public async Task An_empty_string_clears_a_value_and_the_host_default_applies_again()
    {
        var host = HostWithModules();
        var tenant = await TenantAsync();
        var admin = await MemberClientAsync(host, tenant, SystemRoles.AdminRoleId, "Admin");
        var ct = TestContext.Current.CancellationToken;
        (await admin.PutAsJsonAsync("/api/tenant-settings/Email.Smtp",
            new { values = new Dictionary<string, string?> { ["From"] = "own@tenant.example" } }, ct)).IsSuccessStatusCode.Should().BeTrue();

        var cleared = await admin.PutAsJsonAsync("/api/tenant-settings/Email.Smtp",
            new { values = new Dictionary<string, string?> { ["From"] = "" } }, ct);

        cleared.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await cleared.Content.ReadAsStringAsync(ct));
        json.RootElement.GetProperty("keys").EnumerateArray().Single().GetProperty("set").GetBoolean().Should().BeFalse();
        using var scope = host.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ITenantSettings>();
        (await settings.GetOverrideAsync(tenant, "Email.Smtp", "From", ct)).Should().BeNull();
        (await settings.GetAsync(tenant, "Email.Smtp", "From", ct)).Should().Be(HostFrom);
    }

    [Fact]
    public async Task A_change_is_audited_without_the_secret_value()
    {
        var host = HostWithModules();
        var tenant = await TenantAsync();
        var admin = await MemberClientAsync(host, tenant, SystemRoles.AdminRoleId, "Admin");
        var ct = TestContext.Current.CancellationToken;

        (await admin.PutAsJsonAsync("/api/tenant-settings/Files.S3",
            new { values = new Dictionary<string, string?> { ["Bucket"] = "audited-bucket", ["SecretKey"] = SecretValue } }, ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = host.Services.CreateScope();
        var entries = await scope.ServiceProvider.GetRequiredService<IQuerySession>().Query<AuditEvent>()
            .Where(e => e.TenantSlug == tenant && e.Action == "settings.tenant.changed")
            .ToListAsync(ct);
        entries.Should().ContainSingle();
        var entry = entries[0];
        entry.TargetType.Should().Be(nameof(TenantSettings));
        entry.TargetId.Should().Be(TenantSettings.IdForTenant(tenant).ToString());
        entry.Metadata.Should().NotBeNull();
        entry.Metadata!["section"].ToString().Should().Be("Files.S3");
        var recorded = JsonSerializer.Serialize(entry);
        recorded.Should().Contain("SecretKey", "the audit names the field that changed");
        recorded.Should().NotContain(SecretValue, "and never the value");
        recorded.Should().NotContain("audited-bucket", "values are not recorded, only field names");
    }

    [Fact]
    public async Task The_default_tenant_has_no_overrides_so_get_and_put_are_400()
    {
        var host = HostWithModules();
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _factory.CreateToken(roles: ["SuperAdmin"]));
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());

        (await client.GetAsync("/api/tenant-settings/Email.Smtp", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PutAsJsonAsync("/api/tenant-settings/Email.Smtp",
            new { values = new Dictionary<string, string?> { ["From"] = "a@b.example" } },
            TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_module_configuration_endpoint_shows_the_tenant_override_to_a_tenant_caller()
    {
        var host = HostWithModules();
        var tenantA = await TenantAsync();
        var tenantB = await TenantAsync();
        var adminA = await MemberClientAsync(host, tenantA, SystemRoles.AdminRoleId, "Admin");
        (await adminA.PutAsJsonAsync("/api/tenant-settings/Email.Smtp",
            new { values = new Dictionary<string, string?> { ["From"] = "hello@tenant-a.example" } },
            TestContext.Current.CancellationToken)).IsSuccessStatusCode.Should().BeTrue();

        var seenByA = await FromEntryAsync(await MemberClientAsync(host, tenantA, SystemRoles.SuperAdminRoleId, "SuperAdmin"));
        var seenByB = await FromEntryAsync(await MemberClientAsync(host, tenantB, SystemRoles.SuperAdminRoleId, "SuperAdmin"));

        seenByA.GetProperty("value").GetString().Should().Be("hello@tenant-a.example");
        seenByA.GetProperty("source").GetString().Should().Be("tenant");
        seenByB.GetProperty("value").GetString().Should().Be(HostFrom);
        seenByB.GetProperty("source").GetString().Should().NotBe("tenant");
    }

    private static async Task<JsonElement> FromEntryAsync(HttpClient client)
    {
        var res = await client.GetAsync("/api/modules/Email.Smtp/configuration", TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var entries = json.RootElement.GetProperty("entries").EnumerateArray()
            .Where(e => e.GetProperty("key").GetString() == "From").Select(e => e.Clone()).ToArray();
        entries.Should().HaveCount(1);
        return entries[0];
    }

    private static SmtpEmailService Smtp(IServiceScope scope, int port, params string[] fromDomains) =>
        new(Options.Create(new SmtpOptions
            {
                Host = "127.0.0.1", Port = port, From = HostFrom, Security = SmtpSecurity.None,
                TenantFromDomains = [.. fromDomains],
            }).AsSnapshot(),
            scope.ServiceProvider.GetRequiredService<IEmailSettingsProvider>(),
            null,
            scope.ServiceProvider.GetRequiredService<ITenantSettings>());

    private async Task SendForTenantAsync(WebApplicationFactory<Program> host, int port, string tenant, params string[] fromDomains)
    {
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Slug = tenant;
        await ((IEmailService)Smtp(scope, port, fromDomains)).SendForTenantAsync(tenant, "customer@example.com", "Order shipped",
            "<p>Hi</p>", TestContext.Current.CancellationToken);
    }

    private async Task StoreAsync(params object[] documents)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentStore>().LightweightSession();
        foreach (var document in documents)
        {
            session.Store(document);
        }
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string> TenantAsync()
    {
        var slug = Unique("sett");
        await StoreAsync(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
        return slug;
    }

    private async Task<HttpClient> MemberClientAsync(WebApplicationFactory<Program> host, string slug, Guid roleId, string roleName)
    {
        var userId = Guid.NewGuid();
        var username = Unique("u");
        await StoreAsync(
            new User { Id = userId, Username = username, Email = $"{Unique("m")}@example.com" },
            new Membership
            {
                Id = Guid.NewGuid(), UserId = userId, TenantSlug = slug,
                Status = MembershipStatus.Active, RoleIds = [roleId],
            });

        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _factory.CreateToken(
                roles: [roleName],
                userId: userId.ToString(),
                additionalClaims: new Dictionary<string, string> { ["tenant"] = slug, ["Username"] = username }));
        client.DefaultRequestHeaders.Add("X-Tenant", slug);
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, NextIp());
        return client;
    }
}

internal static class OptionsSnapshotExtensions
{
    public static IOptionsSnapshot<T> AsSnapshot<T>(this IOptions<T> options) where T : class, new() =>
        new Snapshot<T>(options.Value);

    private sealed class Snapshot<T>(T value) : IOptionsSnapshot<T> where T : class, new()
    {
        public T Value => value;
        public T Get(string? name) => value;
    }
}
