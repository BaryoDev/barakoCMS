using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Every <c>AddHttpClient</c> registration and every <c>new HttpClient(</c> in production code is
/// either routed through <c>OutboundAddressGuard</c> or named below with the reason its target is
/// set only by the operator. A client a tenant or an admin can aim has to be guarded.
/// </summary>
/// <remarks>
/// Source scans, so they need no database. A new registration fails the first test until it is
/// guarded (the primary handler is <c>OutboundHttpHandler.Create</c> and the client has a timeout)
/// or listed with a reason. An entry naming a registration that no longer exists fails the second,
/// so the list cannot outlive what it excuses. If an exempt client ever starts reading a target from
/// tenant settings or stored data, it leaves the list and is guarded. The key holds the first
/// argument as written, so a second registration in the same file is not covered by the first.
/// </remarks>
public class OutboundClientRegistrationScanTests
{
    // Key: file, then the registration as written. The target of each is set in operator
    // configuration or is a constant in code. No route, tenant setting or stored row sets it.
    internal static readonly Dictionary<string, string> OperatorSetClients = new()
    {
        ["BarakoCMS.AI/AiModule.cs|AddHttpClient<IEmbeddingClient, OllamaEmbeddingClient>()"] =
            "target is Modules:AI:EmbeddingBaseUrl from host configuration, and an operator may point it at a local model on loopback on purpose; the tenant-overridable EmbeddingBaseUrl setting is declared but this client does not read tenant settings",
        ["BarakoCMS.AI/AiModule.cs|AddHttpClient<ICompletionClient, CompletionClient>(c => c.MaxResponseContentBufferSize = 1_000_000)"] =
            "target is the same host configuration or the fixed OpenAI and Anthropic addresses, with a client timeout and a response buffer cap",
        ["BarakoCMS.Diagnostics/DiagnosticsModule.cs|AddHttpClient<SentryForwardClient>()"] =
            "target is the DSN in Modules:Diagnostics:Forwarding from host configuration; redirects are off and the send has its own timeout",
        ["BarakoCMS.ExternalAuth/ExternalAuthModule.cs|AddHttpClient()"] =
            "the unnamed client reaches only the fixed Facebook, GitHub, Google and LinkedIn addresses written as constants in the endpoint files; OpenID Connect providers use the guarded ExternalApi client",
        ["BarakoCMS.Suite/Program.cs|AddHttpClient()"] =
            "registers the unnamed client the ExternalAuth module resolves, so the same fixed provider addresses apply",
    };

    // Key: file, then "new HttpClient(" and its first argument. Empty: production code builds none.
    internal static readonly Dictionary<string, string> DirectHttpClients = new();

    private static readonly Regex Registration = new(@"\.AddHttpClient(?<generics><[^>]*>)?\(", RegexOptions.Compiled);
    // Also the qualified forms and a target-typed new on a field, local or property declared as
    // HttpClient. It cannot see a target-typed new() returned from a method whose return type is HttpClient.
    private static readonly Regex Direct = new(
        @"\bnew\s+(?:global::)?(?:System\s*\.\s*Net\s*\.\s*Http\s*\.\s*)?HttpClient\s*\("
        + @"|\bHttpClient\??\s+\w+\s*(?:\{[^{}]*\}\s*)?=\s*new\s*\(",
        RegexOptions.Compiled);

    private sealed record Found(string Key, string Statement);

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "barakoCMS.sln")))
        {
            root = root.Parent;
        }

        root.Should().NotBeNull("the repository root is above the test output");
        return root!.FullName;
    }

    private static IEnumerable<(string Relative, string Text)> ProductionSources()
    {
        var root = RepositoryRoot();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            var parts = relative.Split('/');
            if (parts[0] is ".claude" or ".playwright-mcp" or "BarakoCMS.Tests" or "BarakoCMS.Testing" or "BarakoCMS.Templates"
                || parts.Contains("obj") || parts.Contains("bin"))
            {
                continue;
            }

            yield return (relative, File.ReadAllText(file));
        }
    }

    private static bool IsCommentLine(string text, int index)
    {
        var lineStart = text.LastIndexOf('\n', index) + 1;
        return text.AsSpan(lineStart, index - lineStart).TrimStart().StartsWith("//");
    }

    /// <summary>
    /// From <paramref name="open"/> (the index of the call's opening parenthesis), the first argument
    /// as written and the end of the statement: the first semicolon at parenthesis, brace and bracket
    /// depth zero, so a lambda with a block body does not cut it short.
    /// </summary>
    private static (string FirstArgument, int End) Read(string text, int open)
    {
        var depth = 0;
        var firstEnd = -1;
        var i = open;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                for (i++; i < text.Length && text[i] != '"'; i++)
                {
                    if (text[i] == '\\')
                    {
                        i++;
                    }
                }

                continue;
            }

            if (c is '(' or '{' or '[')
            {
                depth++;
            }
            else if (c is ')' or '}' or ']')
            {
                depth--;
                if (depth == 0 && firstEnd < 0)
                {
                    firstEnd = i;
                }
            }
            else if (c == ',' && depth == 1 && firstEnd < 0)
            {
                firstEnd = i;
            }
            else if (c == ';' && depth == 0)
            {
                break;
            }
        }

        var first = firstEnd < 0 ? "" : Regex.Replace(text[(open + 1)..firstEnd], @"\s+", " ").Trim();
        return (first, i);
    }

    private static List<Found> Registrations()
    {
        var found = new List<Found>();
        foreach (var (relative, text) in ProductionSources())
        {
            foreach (Match m in Registration.Matches(text))
            {
                if (IsCommentLine(text, m.Index))
                {
                    continue;
                }

                var open = m.Index + m.Length - 1;
                var (first, end) = Read(text, open);
                var statement = text[m.Index..Math.Min(end, text.Length)];
                found.Add(new Found($"{relative}|AddHttpClient{m.Groups["generics"].Value}({first})", statement));
            }
        }

        return found;
    }

    private static List<string> DirectClients()
    {
        var found = new List<string>();
        foreach (var (relative, text) in ProductionSources())
        {
            foreach (Match m in Direct.Matches(text))
            {
                if (!IsCommentLine(text, m.Index))
                {
                    found.Add($"{relative}|new HttpClient({Read(text, m.Index + m.Length - 1).FirstArgument})");
                }
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

    [Fact]
    public void Every_new_HttpClient_in_production_code_is_named_with_a_reason()
    {
        var all = DirectClients();

        all.Where(k => !DirectHttpClients.ContainsKey(k)).Should().BeEmpty(
            "a client built by hand skips the factory and the guard; register one with AddHttpClient or name this one with its reason");
        DirectHttpClients.Keys.Where(k => !all.Contains(k)).Should().BeEmpty();
    }
}
