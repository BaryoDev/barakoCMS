using System.Text.RegularExpressions;
using barakoCMS.Infrastructure.Http;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Every <c>AddHttpClient</c> registration in the solution is either routed through
/// <see cref="OutboundAddressGuard"/> or named below with the reason its target is set only by the
/// operator. A client a tenant or an admin can aim has to be guarded.
/// </summary>
/// <remarks>
/// A new registration fails the first test until it is guarded (the primary handler is
/// <c>OutboundHttpHandler.Create</c> and the client has a timeout) or listed with a reason. An entry
/// naming a registration that no longer exists fails the second, so the list cannot outlive what it
/// excuses. If an exempt client ever starts reading a target from tenant settings or stored data, it
/// leaves this list and is guarded.
/// </remarks>
[Collection("Integration1")]
public class OutboundClientRegistrationTests
{
    // Key: file, then the registration as written. The target of each is set in operator
    // configuration or is a constant in code. No route, tenant setting or stored row sets it.
    internal static readonly Dictionary<string, string> OperatorSetClients = new()
    {
        ["BarakoCMS.AI/AiModule.cs|AddHttpClient<IEmbeddingClient, OllamaEmbeddingClient>"] =
            "target is Modules:AI:EmbeddingBaseUrl from host configuration, and an operator may point it at a local model on loopback on purpose; the tenant-overridable EmbeddingBaseUrl setting is declared but this client does not read tenant settings",
        ["BarakoCMS.AI/AiModule.cs|AddHttpClient<ICompletionClient, CompletionClient>"] =
            "target is the same host configuration or the fixed OpenAI and Anthropic addresses, with a client timeout and a response buffer cap",
        ["BarakoCMS.Diagnostics/DiagnosticsModule.cs|AddHttpClient<SentryForwardClient>"] =
            "target is the DSN in Modules:Diagnostics:Forwarding from host configuration; redirects are off and the send has its own timeout",
        ["BarakoCMS.ExternalAuth/ExternalAuthModule.cs|AddHttpClient"] =
            "the unnamed client reaches only the fixed Facebook, GitHub, Google and LinkedIn addresses written as constants in the endpoint files; OpenID Connect providers use the guarded ExternalApi client",
        ["BarakoCMS.Suite/Program.cs|AddHttpClient"] =
            "registers the unnamed client the ExternalAuth module resolves, so the same fixed provider addresses apply",
    };

    private static readonly Regex Registration = new(@"\.AddHttpClient(?<generics><[^>]*>)?\(", RegexOptions.Compiled);

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "barakoCMS.sln")))
            root = root.Parent;
        root.Should().NotBeNull("the repository root is above the test output");
        return root!.FullName;
    }

    private sealed record Found(string Key, string Statement);

    private static List<Found> Registrations()
    {
        var root = RepositoryRoot();
        var found = new List<Found>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            var parts = relative.Split('/');
            if (parts[0] is ".claude" or ".playwright-mcp" or "BarakoCMS.Tests" or "BarakoCMS.Testing" or "BarakoCMS.Templates"
                || parts.Contains("obj") || parts.Contains("bin"))
                continue;

            var text = File.ReadAllText(file);
            foreach (Match m in Registration.Matches(text))
            {
                var lineStart = text.LastIndexOf('\n', m.Index) + 1;
                if (text.AsSpan(lineStart, m.Index - lineStart).TrimStart().StartsWith("//"))
                    continue;
                var end = text.IndexOf(';', m.Index);
                var statement = text[m.Index..(end < 0 ? text.Length : end)];
                found.Add(new Found($"{relative}|AddHttpClient{m.Groups["generics"].Value}", statement));
            }
        }
        return found;
    }

    private static bool IsGuarded(string statement) =>
        statement.Contains("OutboundHttpHandler.Create(", StringComparison.Ordinal)
        && statement.Contains("Timeout", StringComparison.Ordinal);

    [Fact]
    public void Every_AddHttpClient_registration_is_guarded_or_named_as_operator_set()
    {
        var all = Registrations();
        all.Should().Contain(f => IsGuarded(f.Statement), "the scan has to find the guarded ExternalApi client or it proves nothing");

        var unaccounted = all
            .Where(f => !IsGuarded(f.Statement) && !OperatorSetClients.ContainsKey(f.Key))
            .Select(f => f.Key)
            .ToArray();

        unaccounted.Should().BeEmpty(
            "a client whose target a tenant or an admin can set goes through OutboundHttpHandler.Create with a timeout; "
            + "one set only by the operator is added to OperatorSetClients with the reason");
    }

    [Fact]
    public void The_operator_set_list_names_only_registrations_that_exist()
    {
        var keys = Registrations().Where(f => !IsGuarded(f.Statement)).Select(f => f.Key).ToHashSet();

        OperatorSetClients.Keys.Where(k => !keys.Contains(k)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://127.0.0.1:9/")]
    public async Task ExternalApiClient_RefusesMetadataTarget(string target)
    {
        var client = _factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.ExternalApi);
        client.Timeout.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThan(Timeout.InfiniteTimeSpan);

        var act = () => client.GetAsync(target, TestContext.Current.CancellationToken);

        var thrown = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        Chain(thrown).Should().Contain(e => e is BlockedAddressException, "the guard refused the address before any connection");
    }

    [Fact]
    public void ExternalApiClient_DoesNotFollowARedirect()
    {
        using var handler = OutboundHttpHandler.Create(OutboundAddressGuard.Default);

        handler.AllowAutoRedirect.Should().BeFalse();
    }

    private static IEnumerable<Exception> Chain(Exception e)
    {
        for (var x = e; x is not null; x = x.InnerException!)
            yield return x;
    }

    private readonly IntegrationTestFixture _factory;

    public OutboundClientRegistrationTests(IntegrationTestFixture factory) => _factory = factory;
}
