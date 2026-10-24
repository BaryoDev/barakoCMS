using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// A tenant can send mail from its own address while another tenant keeps the host default (#896).
/// </summary>
[Collection("Integration1")]
public class TenantEmailSettingsTests
{
    private const string TenantKey = "re_tenant_key_nobody_should_ever_see_again";
    private const string HostFrom = "host-default@example.com";

    private readonly IntegrationTestFixture _factory;
    private static int _ipCounter;

    public TenantEmailSettingsTests(IntegrationTestFixture factory) => _factory = factory;

    private static string NextIp() =>
        $"198.51.101.{Interlocked.Increment(ref _ipCounter) % 250 + 1}";

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20].ToLowerInvariant();

    [Fact]
    public async Task A_tenant_override_is_used_for_that_tenant_and_another_keeps_the_host_default()
    {
        var tenantA = await TenantAsync();
        var tenantB = await TenantAsync();
        await SetHostFromAsync(HostFrom);

        var admin = await MemberClientAsync(tenantA, SystemRoles.AdminRoleId, "Admin");
        var put = await admin.PutAsJsonAsync("/api/tenant-settings/email",
            new { apiKey = TenantKey, fromAddress = "hello@tenant-a.example" }, TestContext.Current.CancellationToken);
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());

        (await ResolveAsync(tenantA)).FromAddress.Should().Be("hello@tenant-a.example");
        (await ResolveAsync(tenantB)).FromAddress.Should().Be(HostFrom,
            "tenant B set nothing, so it keeps the host default");
        (await ResolveAsync(Tenant.DefaultSlug)).FromAddress.Should().Be(HostFrom);
    }

    [Fact]
    public async Task A_tenant_that_sets_only_a_sender_keeps_the_host_key_and_the_host_sender()
    {
        var tenant = await TenantAsync();
        var host = await HostAdminAsync();
        (await host.PutAsJsonAsync("/api/settings/email", new { apiKey = "re_host_key_value" },
            TestContext.Current.CancellationToken)).IsSuccessStatusCode.Should().BeTrue();

        await SetHostFromAsync(HostFrom);
        var admin = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");
        (await admin.PutAsJsonAsync("/api/tenant-settings/email", new { fromAddress = "x@tenant.example" },
            TestContext.Current.CancellationToken)).IsSuccessStatusCode.Should().BeTrue();

        var resolved = await ResolveAsync(tenant);
        resolved.FromAddress.Should().Be(HostFrom, "a tenant sender without its own key would send as a host domain");
        resolved.ApiKey.Should().Be("re_host_key_value");
    }

    [Fact]
    public async Task A_tenant_key_is_encrypted_used_for_that_tenant_and_never_returned()
    {
        var tenant = await TenantAsync();
        var other = await TenantAsync();
        var admin = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");

        var put = await admin.PutAsJsonAsync("/api/tenant-settings/email", new { apiKey = TenantKey },
            TestContext.Current.CancellationToken);
        var body = await put.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        put.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().NotContain(TenantKey);

        var read = await (await admin.GetAsync("/api/tenant-settings/email", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        read.Should().NotContain(TenantKey);
        using (var json = JsonDocument.Parse(read))
            json.RootElement.GetProperty("apiKeySet").GetBoolean().Should().BeTrue();

        using (var scope = _factory.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
            var row = await session.LoadAsync<EmailSettings>(EmailSettings.IdForTenant(tenant),
                TestContext.Current.CancellationToken);
            row.Should().NotBeNull();
            row!.TenantSlug.Should().Be(tenant);
            row.ProtectedApiKey.Should().NotBeEmpty().And.NotContain(TenantKey);
        }

        (await ResolveAsync(tenant)).ApiKey.Should().Be(TenantKey);
        (await ResolveAsync(other)).ApiKey.Should().NotBe(TenantKey, "another tenant never sees it");
    }

    [Fact]
    public async Task A_tenant_override_does_not_change_the_host_default_row()
    {
        var tenant = await TenantAsync();
        await SetHostFromAsync(HostFrom);
        var admin = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");
        await admin.PutAsJsonAsync("/api/tenant-settings/email", new { fromAddress = "t@tenant.example" },
            TestContext.Current.CancellationToken);

        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var host = await session.LoadAsync<EmailSettings>(EmailSettings.SingletonId, TestContext.Current.CancellationToken);
        host!.FromAddress.Should().Be(HostFrom);
        host.TenantSlug.Should().BeNull();
    }

    [Fact]
    public async Task A_member_without_the_settings_capability_cannot_change_the_override()
    {
        var tenant = await TenantAsync();
        var member = await MemberClientAsync(tenant, SystemRoles.UserRoleId, "User");

        var res = await member.PutAsJsonAsync("/api/tenant-settings/email", new { fromAddress = "t@tenant.example" },
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Without_a_token_the_override_is_refused()
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/api/tenant-settings/email", TestContext.Current.CancellationToken);
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_default_tenant_has_no_override_and_is_told_where_the_host_default_lives()
    {
        var host = await HostAdminAsync();

        var res = await host.PutAsJsonAsync("/api/tenant-settings/email", new { fromAddress = "t@x.example" },
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("/api/settings/email");
    }

    [Fact]
    public async Task Clearing_the_sender_hands_the_tenant_back_to_the_host_default()
    {
        var tenant = await TenantAsync();
        await SetHostFromAsync(HostFrom);
        var admin = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");

        await admin.PutAsJsonAsync("/api/tenant-settings/email", new { apiKey = TenantKey, fromAddress = "t@tenant.example" },
            TestContext.Current.CancellationToken);
        (await ResolveAsync(tenant)).FromAddress.Should().Be("t@tenant.example");

        await admin.PutAsJsonAsync("/api/tenant-settings/email", new { fromAddress = "" },
            TestContext.Current.CancellationToken);
        (await ResolveAsync(tenant)).FromAddress.Should().Be(HostFrom);
    }

    [Fact]
    public async Task A_tenant_key_that_will_not_decrypt_leaves_the_host_sender_in_force()
    {
        var tenant = await TenantAsync();
        await SetHostFromAsync(HostFrom);
        var otherRing = new barakoCMS.Infrastructure.Security.SecretProtector(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Secrets:Key"] = "some other key material that this deployment never had, 0123456789abcdef",
                })
                .Build());
        await StoreAsync(new EmailSettings
        {
            Id = EmailSettings.IdForTenant(tenant),
            TenantSlug = tenant,
            ProtectedApiKey = otherRing.Protect("placeholder-not-a-key"),
            FromAddress = "ceo@bigbank.example",
        });

        var resolved = await ResolveAsync(tenant);

        resolved.ApiKey.Should().NotBe("placeholder-not-a-key");
        resolved.FromAddress.Should().Be(HostFrom, "the tenant key was not the one in use, so its sender must not be either");
    }

    [Fact]
    public async Task The_plain_lookup_never_returns_a_tenant_key_or_sender()
    {
        var tenant = await TenantAsync();
        await SetHostFromAsync(HostFrom);
        var admin = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");
        (await admin.PutAsJsonAsync("/api/tenant-settings/email",
            new { apiKey = TenantKey, fromAddress = "hello@tenant.example" },
            TestContext.Current.CancellationToken)).IsSuccessStatusCode.Should().BeTrue();

        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Slug = tenant;
        var resolved = await scope.ServiceProvider.GetRequiredService<IEmailSettingsProvider>()
            .GetAsync(TestContext.Current.CancellationToken);

        resolved.ApiKey.Should().NotBe(TenantKey);
        resolved.FromAddress.Should().Be(HostFrom);
    }

    [Fact]
    public async Task Account_mail_under_a_tenant_with_its_own_key_resolves_the_host_key_and_sender()
    {
        var tenant = await TenantAsync();
        await SetHostFromAsync(HostFrom);
        var host = await HostAdminAsync();
        (await host.PutAsJsonAsync("/api/settings/email", new { apiKey = "re_host_key_value" },
            TestContext.Current.CancellationToken)).IsSuccessStatusCode.Should().BeTrue();
        var admin = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");
        (await admin.PutAsJsonAsync("/api/tenant-settings/email",
            new { apiKey = TenantKey, fromAddress = "hello@tenant.example" },
            TestContext.Current.CancellationToken)).IsSuccessStatusCode.Should().BeTrue();

        var sent = await SendUnderTenantAsync(tenant, (email, ct) =>
            email.SendEmailAsync("victim@example.com", "Your sign-in code", "123456", ct));

        sent.Authorization.Should().Be("Bearer re_host_key_value");
        sent.From.Should().Be(HostFrom);
    }

    [Fact]
    public async Task Tenant_business_mail_still_resolves_the_tenant_key_and_sender()
    {
        var tenant = await TenantAsync();
        await SetHostFromAsync(HostFrom);
        var admin = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");
        (await admin.PutAsJsonAsync("/api/tenant-settings/email",
            new { apiKey = TenantKey, fromAddress = "hello@tenant.example" },
            TestContext.Current.CancellationToken)).IsSuccessStatusCode.Should().BeTrue();

        var sent = await SendUnderTenantAsync(tenant, (email, ct) =>
            email.SendForTenantAsync(tenant, "customer@example.com", "Order shipped", "hi", ct));

        sent.Authorization.Should().Be($"Bearer {TenantKey}");
        sent.From.Should().Be("hello@tenant.example");
    }

    [Fact]
    public async Task A_tenant_with_only_a_sender_sends_business_mail_from_the_host_sender()
    {
        var tenant = await TenantAsync();
        await SetHostFromAsync(HostFrom);
        var admin = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");
        (await admin.PutAsJsonAsync("/api/tenant-settings/email", new { fromAddress = "ceo@bigbank.example" },
            TestContext.Current.CancellationToken)).IsSuccessStatusCode.Should().BeTrue();

        var resolved = await ResolveAsync(tenant);
        resolved.FromAddress.Should().Be(HostFrom);
        resolved.FromAddressSource.Should().Be(EmailSettingSource.Stored);

        var sent = await SendUnderTenantAsync(tenant, (email, ct) =>
            email.SendForTenantAsync(tenant, "customer@example.com", "Hello", "hi", ct));
        sent.From.Should().Be(HostFrom);
    }

    [Theory]
    [InlineData("a@b.example\r\nBcc: victim@example.com")]
    [InlineData("a@b.example\nBcc: victim@example.com")]
    [InlineData("not an address")]
    [InlineData("two@one.example, two@two.example")]
    public async Task A_sender_that_is_not_one_clean_address_is_rejected(string from)
    {
        var tenant = await TenantAsync();
        var admin = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");

        var res = await admin.PutAsJsonAsync("/api/tenant-settings/email", new { fromAddress = from },
            TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_sender_with_a_display_name_is_accepted()
    {
        var tenant = await TenantAsync();
        var admin = await MemberClientAsync(tenant, SystemRoles.AdminRoleId, "Admin");

        var res = await admin.PutAsJsonAsync("/api/tenant-settings/email",
            new { fromAddress = "Shop <hello@tenant.example>" }, TestContext.Current.CancellationToken);

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    private sealed record SentMessage(string? Authorization, string? From);

    /// <summary>
    /// Resolves credentials the way a provider does (the host's for an ordinary send, the tenant's for
    /// a tenant send) and records what it would have sent with.
    /// </summary>
    private sealed class ResolvingEmailService(IEmailSettingsProvider settings) : IEmailService
    {
        public SentMessage? Sent { get; private set; }

        public async Task SendEmailAsync(string to, string subject, string body, CancellationToken ct = default) =>
            Record(await settings.GetAsync(ct));

        public async Task SendForTenantAsync(string tenant, string to, string subject, string body, CancellationToken ct = default) =>
            Record(await settings.GetForTenantAsync(tenant, ct));

        private void Record(ResolvedEmailSettings resolved) =>
            Sent = new SentMessage($"Bearer {resolved.ApiKey}", resolved.FromAddress);
    }

    private async Task<SentMessage> SendUnderTenantAsync(string slug, Func<IEmailService, CancellationToken, Task> send)
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Slug = slug;
        var service = new ResolvingEmailService(scope.ServiceProvider.GetRequiredService<IEmailSettingsProvider>());

        await send(service, TestContext.Current.CancellationToken);

        service.Sent.Should().NotBeNull();
        return service.Sent!;
    }

    private async Task<ResolvedEmailSettings> ResolveAsync(string slug)
    {
        using var scope = _factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Slug = slug;
        return await scope.ServiceProvider.GetRequiredService<IEmailSettingsProvider>()
            .GetForTenantAsync(slug, TestContext.Current.CancellationToken);
    }

    private async Task SetHostFromAsync(string from)
    {
        var host = await HostAdminAsync();
        (await host.PutAsJsonAsync("/api/settings/email", new { fromAddress = from },
            TestContext.Current.CancellationToken)).IsSuccessStatusCode.Should().BeTrue();
    }

    private async Task<HttpClient> HostAdminAsync()
    {
        var (token, _) = await TestHelpers.CreateAdminUserAsync(_factory);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task StoreAsync(params object[] documents)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentStore>().LightweightSession();
        foreach (var document in documents) session.Store(document);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string> TenantAsync()
    {
        var slug = Unique("mail");
        await StoreAsync(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug });
        return slug;
    }

    private async Task<HttpClient> MemberClientAsync(string slug, Guid roleId, string roleName)
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

        var client = _factory.CreateClient();
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
