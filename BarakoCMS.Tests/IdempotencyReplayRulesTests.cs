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

    /// <summary>
    /// Opening a share link answers with what the link shows now. A replay would show a revoked
    /// link's content again, so the route never replays even though nothing in its shape says so.
    /// </summary>
    [Fact]
    public void Opening_a_share_link_never_replays()
    {
        typeof(barakoCMS.Features.Site.ShareLinks.OpenShareLinkEndpoint)
            .IsDefined(typeof(NoIdempotentReplayAttribute), inherit: true).Should().BeTrue();
    }

    /// <summary>Property names a response carrying a credential uses here.</summary>
    private static readonly HashSet<string> ResponseCredentialNames =
    [
        "Token", "RefreshToken", "AccessToken", "MfaChallengeToken", "ChallengeToken",
        "Secret", "ClientSecret", "RecoveryCodes", "Key", "ApiKey", "Password", "Code",
    ];

    /// <summary>Property names a request carrying a password uses here.</summary>
    private static readonly HashSet<string> RequestPasswordNames = ["Password", "NewPassword", "CurrentPassword"];

    /// <summary>Types with one of those names that hold no credential, and why.</summary>
    private static readonly Dictionary<string, string> NotCredentials = new()
    {
        ["BarakoCMS.FeatureFlags.FlagDto"] = "Key is the flag's name",
        ["barakoCMS.Features.Settings.SystemSettingDto"] = "Key is the setting's name",
        ["BarakoCMS.Accounting.Domain.Account"] = "Code is the account code",
        ["BarakoCMS.Accounting.AccountBalance"] = "Code is the account code",
        ["BarakoCMS.Accounting.Features.Accounts.CreateAccountEndpoint+Result"] = "Code is the account code",
    };

    /// <summary>
    /// Every barakoCMS assembly next to the tests: the core and each module the suite builds, loaded
    /// from the output directory rather than from what this assembly happens to reference.
    /// </summary>
    private static List<Assembly> BarakoAssemblies()
    {
        var assemblies = Directory.GetFiles(AppContext.BaseDirectory, "*.dll")
            .Where(path =>
            {
                var file = Path.GetFileNameWithoutExtension(path);
                return (file == "barakoCMS" || file.StartsWith("BarakoCMS.", StringComparison.Ordinal))
                       && file != "BarakoCMS.Tests";
            })
            .Select(path => Assembly.Load(AssemblyName.GetAssemblyName(path)))
            .ToList();

        assemblies.Should().Contain(typeof(barakoCMS.Data.DataSeeder).Assembly);
        return assemblies;
    }

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    /// <summary>
    /// A stored response would be a second, replayable copy of a secret the API otherwise keeps only
    /// as a hash, and a stored request hash of a password body is something to guess against. So an
    /// endpoint that answers with a credential anywhere in its response shape, or takes a password,
    /// must say it never replays.
    /// </summary>
    /// <remarks>
    /// The response is walked through generic arguments (lists, pages, <c>Results&lt;...&gt;</c>
    /// unions) and nested barakoCMS types. An endpoint with no typed response (<c>Endpoint&lt;TRequest&gt;</c>,
    /// a plain <c>EndpointWithoutRequest</c>) writes whatever its handler builds, which reflection
    /// cannot see; such a route that returns a credential has to be marked by hand.
    /// </remarks>
    [Fact]
    public void Every_endpoint_that_returns_a_credential_or_takes_a_password_never_replays()
    {
        var endpoints = BarakoAssemblies()
            .SelectMany(LoadableTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Select(t => (Endpoint: t, Shape: EndpointShape(t)))
            .Where(e => e.Shape is not null)
            .ToList();

        var flagged = endpoints
            .Where(e => (e.Shape!.Value.Response is { } response && CarriesName(response, ResponseCredentialNames))
                        || (e.Shape!.Value.Request is { } request && CarriesName(request, RequestPasswordNames)))
            .Select(e => e.Endpoint)
            .ToList();

        flagged.Should().NotBeEmpty("the sign-in, API key and password endpoints carry credentials");
        flagged.Should().Contain(typeof(barakoCMS.Features.ApiKeys.CreateApiKeyEndpoint));
        flagged.Where(t => !t.IsDefined(typeof(NoIdempotentReplayAttribute), inherit: true))
            .Select(t => t.FullName)
            .Should().BeEmpty("each of these carries a credential or a password and must have [NoIdempotentReplay]");
    }

    private static bool CarriesName(Type root, HashSet<string> names)
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<(Type Type, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            var (type, depth) = queue.Dequeue();
            if (depth > 5 || !seen.Add(type))
            {
                continue;
            }

            if (type.IsArray)
            {
                queue.Enqueue((type.GetElementType()!, depth + 1));
                continue;
            }

            if (type.IsGenericType)
            {
                foreach (var arg in type.GetGenericArguments())
                {
                    queue.Enqueue((arg, depth + 1));
                }
            }

            if (!IsOurs(type) || type.IsEnum || NotCredentials.ContainsKey(type.FullName ?? ""))
            {
                continue;
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (names.Contains(property.Name))
                {
                    return true;
                }
                queue.Enqueue((property.PropertyType, depth + 1));
            }
        }

        return false;
    }

    private static bool IsOurs(Type type) =>
        type.Namespace is { } ns
        && (ns.StartsWith("barakoCMS", StringComparison.Ordinal) || ns.StartsWith("BarakoCMS", StringComparison.Ordinal));

    private static (Type? Request, Type? Response)? EndpointShape(Type endpoint)
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
                return (null, args[0]);
            }
            if (name.StartsWith("Endpoint`", StringComparison.Ordinal))
            {
                return (args[0], args.Length >= 2 ? args[1] : null);
            }
        }

        return null;
    }
}
