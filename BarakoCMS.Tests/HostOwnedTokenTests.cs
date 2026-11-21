using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints.Security;
using FluentAssertions;
using Marten;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A host mints and validates its own tokens, whatever another host in the process did (#1408).
/// </summary>
/// <remarks>
/// FastEndpoints keeps the validation key and the service resolver in process-wide statics, which the
/// last host to start owns. Each test here starts hosts on the fixture's database with a signing key of
/// their own and shows what one host does with the other's token, or with a disposed neighbour.
/// Serial, because the last test turns the resolver repair off and disposes a host the static points at.
/// </remarks>
[Collection("Integration1")]
public class HostOwnedTokenTests(IntegrationTestFixture factory)
{
    private const string FirstKey = "first-host-signing-key-that-is-at-least-32-chars"; // allow-secret
    private const string SecondKey = "second-host-signing-key-that-is-at-least-32-chars"; // allow-secret

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> HostWithKey(string key)
    {
        var host = factory.WithSetting("JWT:Key", key);
        _ = host.Services;
        return host;
    }

    private async Task<Guid> StoredUserAsync()
    {
        using var scope = factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var id = Guid.NewGuid();
        session.Store(new User { Id = id, Username = $"own-{id:n}", Email = $"own-{id:n}@example.com", RoleIds = [] });
        await session.SaveChangesAsync(Ct);
        return id;
    }

    private static string MintOn(WebApplicationFactory<Program> host, Guid userId)
    {
        var config = host.Services.GetRequiredService<IConfiguration>();
        return host.Services.GetRequiredService<IJwtMinter>().Mint(
            config["JWT:Audience"],
            DateTime.UtcNow.AddMinutes(5),
            [
                new Claim("UserId", userId.ToString()),
                new Claim("Username", $"own-{userId:n}"),
                new Claim("tenant", Tenant.DefaultSlug),
                new Claim(ClaimTypes.Role, "User"),
            ]);
    }

    private static async Task<HttpStatusCode> WhoAmIAsync(WebApplicationFactory<Program> host, string token)
    {
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.GetAsync("/api/me", Ct);
        return response.StatusCode;
    }

    [Fact]
    public async Task Two_hosts_with_different_signing_keys_each_accept_only_their_own_tokens()
    {
        var userId = await StoredUserAsync();
        var first = HostWithKey(FirstKey);
        var second = HostWithKey(SecondKey);
        var firstToken = MintOn(first, userId);
        var secondToken = MintOn(second, userId);

        var results = new[]
        {
            await WhoAmIAsync(first, firstToken),
            await WhoAmIAsync(second, secondToken),
            await WhoAmIAsync(first, secondToken),
            await WhoAmIAsync(second, firstToken),
        };

        results.Should().HaveCount(4);
        results.Should().Equal(
            HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_host_still_mints_and_accepts_a_token_after_another_host_is_disposed()
    {
        var userId = await StoredUserAsync();
        var first = HostWithKey(FirstKey);
        var second = HostWithKey(SecondKey);

        using (FastEndpointsResolver.WithoutRepair())
        {
            second.Dispose();
            FastEndpointsResolver.IsUsable().Should().BeFalse(
                "the second host started last, so the static resolver points at it, and it is disposed");

            var token = MintOn(first, userId);
            var status = await WhoAmIAsync(first, token);

            status.Should().Be(HttpStatusCode.OK, "the first host signs and validates without the static resolver");
        }
    }

    [Fact]
    public void A_host_registers_no_JwtCreationOptions_for_the_minter_to_ignore()
    {
        var services = factory.Services;

        services.GetServices<IConfigureOptions<JwtCreationOptions>>().Should().HaveCount(0);
        services.GetServices<IPostConfigureOptions<JwtCreationOptions>>().Should().HaveCount(0);
        services.GetServices<IOptionsChangeTokenSource<JwtCreationOptions>>().Should().HaveCount(0);
        services.GetRequiredService<IOptions<JwtCreationOptions>>().Value.SigningKey
            .Should().BeNull("the minter reads JWT:Key, so nothing configured a signing key for JwtBearer to use");
    }

    [Fact]
    public void The_options_check_sees_a_JwtCreationOptions_registration_when_there_is_one()
    {
        var services = new ServiceCollection();
        services.Configure<JwtCreationOptions>(o => o.SigningKey = "x");
        using var provider = services.BuildServiceProvider();

        provider.GetServices<IConfigureOptions<JwtCreationOptions>>().Should().HaveCount(1);
    }

    [Fact]
    public void A_key_made_weak_in_the_host_configuration_is_refused_when_minting_and_when_the_bearer_options_are_built()
    {
        // The startup guard read the key as it was when AddBarakoCMS ran. The key is checked again
        // where it is used, so a weak key set on the host's configuration afterwards is refused.
        var host = HostWithKey(FirstKey);
        var config = host.Services.GetRequiredService<IConfiguration>();
        var original = config["JWT:Key"];
        try
        {
            config["JWT:Key"] = "too-short"; // allow-secret

            var mint = () => MintOn(host, Guid.NewGuid());
            mint.Should().Throw<InvalidOperationException>().WithMessage("*JWT:Key*");

            var build = () => host.Services.GetRequiredService<IOptionsFactory<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>>()
                .Create(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme);
            build.Should().Throw<InvalidOperationException>().WithMessage("*JWT:Key*");
        }
        finally
        {
            config["JWT:Key"] = original;
        }
    }
}
