using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BarakoCMS.Tests.Features.Auth;

/// <summary>GET /api/auth/methods lists the ids of the ways in, and nothing else (#1253).</summary>
[Collection("Integration1")]
public class SignInMethodsTests
{
    private static int _ip;

    private readonly IntegrationTestFixture _fixture;

    public SignInMethodsTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class NamedMethod(string name) : ISignInMethod
    {
        public string Name => name;
    }

    private sealed class SmtpStyleEmail : IEmailService
    {
        public Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoKey : IEmailSettingsProvider
    {
        public Task<ResolvedEmailSettings> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(new ResolvedEmailSettings(null, null, EmailSettingSource.None, EmailSettingSource.None));
    }

    private static HttpClient WithIp(HttpClient client, string? slug = null)
    {
        client.DefaultRequestHeaders.Add(TestRemoteIpFilter.Header, $"2001:db8:1253:{Interlocked.Increment(ref _ip):x}::1");
        if (slug is not null)
        {
            client.DefaultRequestHeaders.Add("X-Tenant", slug);
        }

        return client;
    }

    private HttpClient From(string? slug = null) => WithIp(_fixture.CreateClient(), slug);

    private static async Task<string[]> ListAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/auth/methods", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.EnumerateObject().Select(p => p.Name).Should().Equal("methods");
        return body.GetProperty("methods").EnumerateArray().Select(e => e.GetString()!).ToArray();
    }

    private async Task<string> TenantAsync(TenantSignInPolicy signIn)
    {
        var slug = $"sm-{Guid.NewGuid():N}"[..16];
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new Tenant { Id = Guid.NewGuid(), Slug = slug, Name = slug, SignIn = signIn });
        await session.SaveChangesAsync(Ct);
        return slug;
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> HostWith(string id) =>
        _fixture.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<ISignInMethod>(new NamedMethod(id))));

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> MockMailHost() =>
        _fixture.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IEmailService>();
            s.AddSingleton<IEmailService, MockEmailService>();
        }));

    [Fact]
    public async Task With_no_module_announcing_a_method_the_list_is_password_and_email_code()
    {
        var methods = await ListAsync(From());

        methods.Should().HaveCount(2);
        methods.Should().Equal("password", "email-code");
    }

    [Fact]
    public async Task A_module_that_announces_test_method_makes_the_list_three()
    {
        using var host = HostWith("test-method");

        var methods = await ListAsync(WithIp(host.CreateClient()));

        methods.Should().HaveCount(3);
        methods.Should().Equal("password", "email-code", "test-method");
    }

    [Fact]
    public async Task The_mock_email_provider_does_not_list_email_code()
    {
        using var host = MockMailHost();

        var methods = await ListAsync(WithIp(host.CreateClient()));

        methods.Should().Equal("password");
    }

    [Fact]
    public async Task A_provider_that_needs_no_api_key_lists_email_code()
    {
        using var host = _fixture.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IEmailService>();
            s.AddSingleton<IEmailService, SmtpStyleEmail>();
            s.RemoveAll<IEmailSettingsProvider>();
            s.AddScoped<IEmailSettingsProvider, NoKey>();
        }));

        var methods = await ListAsync(WithIp(host.CreateClient()));

        methods.Should().Equal("password", "email-code");
    }

    [Fact]
    public async Task A_tenant_that_does_not_allow_otp_lists_password_only()
    {
        var slug = await TenantAsync(new TenantSignInPolicy { Methods = [TenantSignInPolicy.Password] });

        var methods = await ListAsync(From(slug));

        methods.Should().Equal("password");
    }

    [Fact]
    public async Task A_tenant_lists_a_module_method_only_when_its_policy_names_it()
    {
        var plain = await TenantAsync(new TenantSignInPolicy());
        var naming = await TenantAsync(new TenantSignInPolicy { Methods = ["test-method"] });
        using var host = HostWith("test-method");

        (await ListAsync(WithIp(host.CreateClient(), plain))).Should().Equal("password", "email-code");
        (await ListAsync(WithIp(host.CreateClient(), naming))).Should().Equal("test-method");
    }

    [Fact]
    public async Task An_unknown_tenant_gets_the_same_body_as_the_default_tenant()
    {
        var known = await (await From().GetAsync("/api/auth/methods", Ct)).Content.ReadAsStringAsync(Ct);
        var unknown = await (await From($"nope-{Guid.NewGuid():N}"[..16]).GetAsync("/api/auth/methods", Ct)).Content.ReadAsStringAsync(Ct);

        known.Should().Contain("password");
        unknown.Should().Be(known);
    }

    [Theory]
    [InlineData("otp")]
    [InlineData("social")]
    [InlineData("password")]
    [InlineData("email-code")]
    public async Task A_module_cannot_announce_a_built_in_id(string id)
    {
        using var host = HostWith(id);

        var methods = await ListAsync(WithIp(host.CreateClient()));

        methods.Should().Equal("password", "email-code");
    }

    [Theory]
    [InlineData("password")]
    [InlineData("otp")]
    [InlineData("social")]
    public async Task Password_and_email_code_are_listed_exactly_when_login_and_otp_request_are_not_refused(string only)
    {
        var slug = await TenantAsync(new TenantSignInPolicy { Methods = [only] });
        var client = From(slug);

        var methods = await ListAsync(client);
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "nobody", password = "wrong" }, Ct);
        var otp = await client.PostAsJsonAsync("/api/auth/otp/request", new { email = "nobody@example.com" }, Ct);

        methods.Contains("password").Should().Be(login.StatusCode != HttpStatusCode.Forbidden);
        methods.Contains("email-code").Should().Be(otp.StatusCode != HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task With_the_mock_email_provider_email_code_is_absent_though_otp_request_still_answers_200()
    {
        using var host = MockMailHost();
        var client = WithIp(host.CreateClient());

        (await ListAsync(client)).Should().NotContain("email-code");
        (await client.PostAsJsonAsync("/api/auth/otp/request", new { email = "nobody@example.com" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_list_is_public_and_carries_only_method_ids()
    {
        var response = await From().GetAsync("/api/auth/methods", Ct);
        var raw = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        raw.Should().NotContain(IntegrationTestFixture.ConfiguredEmailKey);
        var methods = JsonDocument.Parse(raw).RootElement.GetProperty("methods").EnumerateArray().ToList();
        methods.Should().NotBeEmpty();
        methods.Should().OnlyContain(e => e.ValueKind == JsonValueKind.String);
    }
}
