using System.Text;
using BarakoCMS.Secrets.Aws;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;
using Host = barakoCMS.Extensions.ServiceCollectionExtensions;

namespace BarakoCMS.Tests;

/// <summary>
/// The AWS source end to end: the host starts on the key from the store and signs with it.
/// </summary>
/// <remarks>
/// In the shared collection because FastEndpoints creates a token through its process-wide service
/// resolver, which the fixture's host sets. This class only reads it.
/// </remarks>
[Collection("Sequential")]
public class AwsSecretsHostTests
{
    private const string StoreJwtKey = "jwt-key-from-the-store-that-is-at-least-32-chars";

    public AwsSecretsHostTests(IntegrationTestFixture fixture) => _ = fixture;

    /// <summary>
    /// The ticket's done-when: JWT:Key absent from the configuration the host was given, and tokens
    /// signed with the value from the store. Signed through MfaChallengeToken, which reads the key
    /// from configuration the way the access token issuer does and needs no database, then checked
    /// with a handler of the test's own, so nothing of the host's validates its own work.
    /// </summary>
    [Fact]
    public void The_host_starts_and_signs_tokens_with_the_key_from_the_store()
    {
        const string environmentKey = "jwt-key-from-the-environment-at-least-32-chars";
        var configuration = Configuration(
            ("ConnectionStrings:DefaultConnection", "Host=localhost;Database=none"),
            ("JWT:Issuer", "barako"),
            ("JWT:Audience", "barako"),
            ("Secrets:Aws:Map:JWT:Key", "barakocms-jwt-key"));
        configuration["JWT:Key"].Should().BeNull("the key must come only from the store for this to prove anything");

        configuration.AddBarakoAwsSecrets((id, _) => Task.FromResult<string?>(id == "barakocms-jwt-key" ? StoreJwtKey : null));
        var start = () => Host.AddBarakoCMS(new ServiceCollection(), configuration, configureModules: null);
        start.Should().NotThrow("the JWT key guard passes on the key the store supplied");

        var (token, _) = barakoCMS.Infrastructure.Auth.Mfa.MfaChallengeToken.Create(configuration, Guid.NewGuid());

        SignatureValidUnder(token, StoreJwtKey).Should().BeTrue();
        SignatureValidUnder(token, environmentKey).Should().BeFalse("a token signed with any other key is the bug");
    }

    private static bool SignatureValidUnder(string token, string key)
    {
        var parameters = new TokenValidationParameters
        {
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            ValidateIssuerSigningKey = true,
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = false,
        };
        return new JsonWebTokenHandler().ValidateTokenAsync(token, parameters).GetAwaiter().GetResult().IsValid;
    }

    private static ConfigurationManager Configuration(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)));
        return configuration;
    }
}
