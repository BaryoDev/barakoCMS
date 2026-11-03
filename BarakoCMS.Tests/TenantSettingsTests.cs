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
        await SendForTenantAsync(host, relay.Port, tenantA);
        await SendForTenantAsync(host, relay.Port, tenantB);

        var messages = relay.Messages;
        messages.Should().HaveCount(2);
        messages[0].Should().Contain("hello@tenant-a.example");
        messages[0].Should().NotContain(HostFrom);
        messages[1].Should().Contain(HostFrom, "tenant B set nothing, so it keeps the host default");
        messages[1].Should().NotContain("tenant-a.example");
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
        await Smtp(scope, relay.Port).SendEmailAsync("someone@example.com", "Your sign-in code", "<p>1</p>",
            TestContext.Current.CancellationToken);

        var messages = relay.Messages;
        messages.Should().HaveCount(1);
        messages[0].Should().Contain(HostFrom).And.NotContain("bigbank.example");
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

        // The module configuration view and the resolver's plain read are the other two ways out.
        var config = await admin.GetAsync("/api/modules/Files.S3/configuration", TestContext.Current.CancellationToken);
        (await config.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().NotContain(SecretValue);

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

        (await member.PutAsJsonAsync("/api/tenant-settings/Email.Smtp",
            new { values = new Dictionary<string, string?> { ["From"] = "a@b.example" } },
            TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
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

    private static SmtpEmailService Smtp(IServiceScope scope, int port) =>
        new(Options.Create(new SmtpOptions
            {
                Host = "127.0.0.1", Port = port, From = HostFrom, Security = SmtpSecurity.None,
            }).AsSnapshot(),
            scope.ServiceProvider.GetRequiredService<IEmailSettingsProvider>(),
            null,
            scope.ServiceProvider.GetRequiredService<ITenantSettings>());

    private async Task SendForTenantAsync(WebApplicationFactory<Program> host, int port, string tenant)
    {
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Slug = tenant;
        await ((IEmailService)Smtp(scope, port)).SendForTenantAsync(tenant, "customer@example.com", "Order shipped",
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
