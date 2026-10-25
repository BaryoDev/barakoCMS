using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
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
    private readonly IntegrationTestFixture _fixture;

    public SignInMethodsTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class TestMethod : ISignInMethod
    {
        public string Name => "test-method";
    }

    private sealed class NoTransport : IEmailSettingsProvider
    {
        public Task<ResolvedEmailSettings> GetAsync(CancellationToken ct = default) =>
            Task.FromResult(new ResolvedEmailSettings(null, null, EmailSettingSource.None, EmailSettingSource.None));
    }

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

    private static HttpClient As(HttpClient client, string slug)
    {
        client.DefaultRequestHeaders.Add("X-Tenant", slug);
        return client;
    }

    [Fact]
    public async Task With_no_module_announcing_a_method_the_list_is_password_and_email_code()
    {
        var methods = await ListAsync(_fixture.CreateClient());

        methods.Should().HaveCount(2);
        methods.Should().Equal("password", "email-code");
    }

    [Fact]
    public async Task A_module_that_announces_test_method_makes_the_list_three()
    {
        using var host = _fixture.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<ISignInMethod>(new TestMethod())));

        var methods = await ListAsync(host.CreateClient());

        methods.Should().HaveCount(3);
        methods.Should().Equal("password", "email-code", "test-method");
    }

    [Fact]
    public async Task Without_an_email_transport_email_code_is_not_listed()
    {
        using var host = _fixture.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IEmailSettingsProvider>();
            s.AddScoped<IEmailSettingsProvider, NoTransport>();
        }));

        var methods = await ListAsync(host.CreateClient());

        methods.Should().Equal("password");
    }

    [Fact]
    public async Task A_tenant_that_does_not_allow_otp_lists_password_only()
    {
        var slug = await TenantAsync(new TenantSignInPolicy { Methods = [TenantSignInPolicy.Password] });

        var methods = await ListAsync(As(_fixture.CreateClient(), slug));

        methods.Should().Equal("password");
    }

    [Fact]
    public async Task A_tenant_lists_a_module_method_only_when_its_policy_names_it()
    {
        var plain = await TenantAsync(new TenantSignInPolicy());
        var naming = await TenantAsync(new TenantSignInPolicy { Methods = ["test-method"] });
        using var host = _fixture.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<ISignInMethod>(new TestMethod())));

        (await ListAsync(As(host.CreateClient(), plain))).Should().Equal("password", "email-code");
        (await ListAsync(As(host.CreateClient(), naming))).Should().Equal("test-method");
    }

    [Fact]
    public async Task The_list_is_public_and_carries_only_method_ids()
    {
        var response = await _fixture.CreateClient().GetAsync("/api/auth/methods", Ct);
        var raw = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        raw.Should().NotContain(IntegrationTestFixture.ConfiguredEmailKey);
        var methods = JsonDocument.Parse(raw).RootElement.GetProperty("methods").EnumerateArray().ToList();
        methods.Should().NotBeEmpty();
        methods.Should().OnlyContain(e => e.ValueKind == JsonValueKind.String);
    }
}
