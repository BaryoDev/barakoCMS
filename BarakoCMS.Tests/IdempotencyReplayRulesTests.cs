using System.Reflection;
using barakoCMS.Infrastructure.Filters;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

public class IdempotencyReplayRulesTests
{
    [Theory]
    [InlineData(200, true)]
    [InlineData(201, true)]
    [InlineData(204, true)]
    [InlineData(301, false)]
    [InlineData(302, false)]
    [InlineData(304, false)]
    [InlineData(400, false)]
    [InlineData(409, false)]
    [InlineData(500, false)]
    public void Only_a_2xx_keeps_the_key(int status, bool kept)
    {
        IdempotencyFinalizer.Succeeded(status, exceptionOccurred: false, validationFailed: false).Should().Be(kept);
    }

    [Fact]
    public void A_thrown_handler_or_a_validation_failure_releases_the_key_whatever_the_status()
    {
        IdempotencyFinalizer.Succeeded(200, exceptionOccurred: true, validationFailed: false).Should().BeFalse();
        IdempotencyFinalizer.Succeeded(200, exceptionOccurred: false, validationFailed: true).Should().BeFalse();
    }

    /// <summary>Property names a response carrying a credential uses here.</summary>
    private static readonly HashSet<string> CredentialNames =
        ["Token", "RefreshToken", "MfaChallengeToken", "ChallengeToken", "Secret", "RecoveryCodes", "Key"];

    /// <summary>Responses with one of those names that hold no credential, and why.</summary>
    private static readonly Dictionary<string, string> NotCredentials = new()
    {
        ["BarakoCMS.FeatureFlags.FlagDto"] = "Key is the flag's name",
    };

    /// <summary>
    /// A stored response would be a second, replayable copy of a secret the API otherwise keeps only
    /// as a hash, so every endpoint that answers with one must say it never replays.
    /// </summary>
    [Fact]
    public void Every_endpoint_that_returns_a_credential_never_replays()
    {
        var assemblies = typeof(IdempotencyReplayRulesTests).Assembly.GetReferencedAssemblies()
            .Where(n => n.Name is not null
                        && (n.Name == "barakoCMS" || n.Name.StartsWith("BarakoCMS.", StringComparison.Ordinal))
                        && n.Name != "BarakoCMS.Tests")
            .Select(Assembly.Load)
            .Append(typeof(barakoCMS.Data.DataSeeder).Assembly)
            .Distinct()
            .ToList();

        var withCredentials = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Select(t => (Endpoint: t, Response: ResponseType(t)))
            .Where(e => e.Response is not null
                        && !NotCredentials.ContainsKey(e.Response.FullName ?? "")
                        && e.Response.GetProperties().Any(p => CredentialNames.Contains(p.Name)))
            .ToList();

        withCredentials.Should().NotBeEmpty("the login and API key endpoints answer with credentials");
        withCredentials.Select(e => e.Endpoint)
            .Where(t => !t.IsDefined(typeof(NoIdempotentReplayAttribute), inherit: true))
            .Select(t => t.FullName)
            .Should().BeEmpty("each of these answers with a credential and must carry [NoIdempotentReplay]");
    }

    private static Type? ResponseType(Type endpoint)
    {
        for (var t = endpoint.BaseType; t is not null; t = t.BaseType)
        {
            if (!t.IsGenericType || t.Namespace != "FastEndpoints")
            {
                continue;
            }

            var name = t.GetGenericTypeDefinition().Name;
            var args = t.GetGenericArguments();
            if (name.StartsWith("EndpointWithoutRequest`", StringComparison.Ordinal))
            {
                return args[0];
            }
            if (name.StartsWith("Endpoint`", StringComparison.Ordinal) && args.Length >= 2)
            {
                return args[1];
            }
        }

        return null;
    }
}
