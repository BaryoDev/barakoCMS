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
        ["BarakoCMS.AI/AiModule.cs|AddHttpClient<IEmbeddingClient, OllamaEmbeddingClient>(OllamaEmbeddingClient.Configure)"] =
            "target is Modules:AI:EmbeddingBaseUrl from host configuration, and an operator may point it at a local model on loopback on purpose; the tenant-overridable EmbeddingBaseUrl setting is declared but this client does not read tenant settings; Configure gives it a 30 second timeout and a 1 MB response buffer cap",
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
    internal static readonly Dictionary<string, string> DirectHttpClients = new()
    {
        ["barakoCMS/Infrastructure/Health/HealthProbe.cs|new HttpClient(handler)"] =
            "the health probe runs before the host exists, so there is no factory; it only calls 127.0.0.1 on the port from ASPNETCORE_URLS, with its own timeout, no proxy and redirects off",
    };

    // The dot may be followed by whitespace or a newline (a chained call split over lines), and the
    // type arguments may nest, so the angle brackets are balanced rather than cut at the first ">".
    private static readonly Regex Registration = new(
        @"\.\s*AddHttpClient\s*(?<generics><(?:[^<>]+|<(?<d>)|>(?<-d>))*(?(d)(?!))>)?\s*\(",
        RegexOptions.Compiled);
    // Also the qualified forms, an object initializer with no parentheses, and a target-typed new on
    // a field, local or property declared as HttpClient. It cannot see a target-typed new() returned
    // from a method whose return type is HttpClient.
    private static readonly Regex Direct = new(
        @"\bnew\s+(?:global::)?(?:System\s*\.\s*Net\s*\.\s*Http\s*\.\s*)?HttpClient\s*[({]"
        + @"|\bHttpClient\??\s+\w+\s*(?:\{[^{}]*\}\s*)?=\s*new\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex TimeoutAssignment = new(@"\bTimeout\s*=(?![=>])", RegexOptions.Compiled);
    private static readonly Regex CreateClientCall = new(@"\bCreateClient\s*\(", RegexOptions.Compiled);

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

            yield return (relative, WithoutComments(File.ReadAllText(file)));
        }
    }

    /// <summary>
    /// The text with every <c>//</c> and <c>/* */</c> comment blanked, newlines kept so positions do
    /// not move. String, verbatim string and char literals are walked over, so a slash pair inside
    /// one is not a comment. It does not handle raw (triple quote) strings.
    /// </summary>
    internal static string WithoutComments(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            var next = i + 1 < chars.Length ? chars[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                for (; i < chars.Length && chars[i] != '\n'; i++)
                {
                    chars[i] = ' ';
                }

                i--;
            }
            else if (c == '/' && next == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var end = close < 0 ? chars.Length : close + 2;
                for (; i < end; i++)
                {
                    if (chars[i] is not ('\n' or '\r'))
                    {
                        chars[i] = ' ';
                    }
                }

                i--;
            }
            else if (c == '"' || (c == '@' && next is '"' or '$') || (c == '$' && next == '@' && i + 2 < chars.Length && chars[i + 2] == '"'))
            {
                var verbatim = c != '"';
                while (chars[i] != '"')
                {
                    i++;
                }

                for (i++; i < chars.Length; i++)
                {
                    if (verbatim && chars[i] == '"')
                    {
                        if (i + 1 < chars.Length && chars[i + 1] == '"')
                        {
                            i++;
                            continue;
                        }

                        break;
                    }

                    if (!verbatim && chars[i] == '\\')
                    {
                        i++;
                    }
                    else if (!verbatim && chars[i] == '"')
                    {
                        break;
                    }
                }
            }
            else if (c == '\'')
            {
                for (i++; i < chars.Length; i++)
                {
                    if (chars[i] == '\\')
                    {
                        i++;
                    }
                    else if (chars[i] == '\'')
                    {
                        break;
                    }
                }
            }
        }

        return new string(chars);
    }

    /// <summary>
    /// From <paramref name="open"/> (the index of the call's opening parenthesis), the first argument
    /// as written, the call's code with comments removed, and the end of the statement: the first
    /// semicolon at parenthesis, brace and bracket depth zero, so a lambda with a block body does
    /// not cut it short. Comments, string literals, verbatim strings and char literals are skipped for
    /// depth and for the semicolon. It does not handle raw (triple quote) strings or interpolation
    /// holes that hold quotes.
    /// </summary>
    private static (string FirstArgument, string Code, int End) Read(string text, int open)
    {
        var code = new StringBuilder();
        var depth = 0;
        var firstEnd = -1;
        var i = open;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                code.Append('\n');
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? text.Length : close + 1;
                code.Append(' ');
                continue;
            }

            var verbatim = c == '@' && i + 1 < text.Length && text[i + 1] == '"';
            if (c == '"' || verbatim)
            {
                if (verbatim)
                {
                    code.Append(c);
                    i++;
                }

                code.Append(text[i]);
                for (i++; i < text.Length; i++)
                {
                    code.Append(text[i]);
                    if (verbatim && text[i] == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            code.Append('"');
                            i++;
                            continue;
                        }

                        break;
                    }

                    if (!verbatim && text[i] == '\\' && i + 1 < text.Length)
                    {
                        code.Append(text[++i]);
                    }
                    else if (!verbatim && text[i] == '"')
                    {
                        break;
                    }
                }

                continue;
            }

            if (c == '\'')
            {
                code.Append(c);
                for (i++; i < text.Length; i++)
                {
                    code.Append(text[i]);
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        code.Append(text[++i]);
                    }
                    else if (text[i] == '\'')
                    {
                        break;
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
                    firstEnd = code.Length;
                }
            }
            else if (c == ',' && depth == 1 && firstEnd < 0)
            {
                firstEnd = code.Length;
            }
            else if (c == ';' && depth == 0)
            {
                break;
            }

            code.Append(c);
        }

        var all = code.ToString();
        var first = firstEnd < 0 ? "" : Regex.Replace(all[1..firstEnd], @"\s+", " ").Trim();
        return (first, all, i);
    }

    private static List<Found> Registrations()
    {
        var found = new List<Found>();
        foreach (var (relative, text) in ProductionSources())
        {
            foreach (Match m in Registration.Matches(text))
            {
                var open = m.Index + m.Length - 1;
                var (first, code, _) = Read(text, open);
                var statement = text[m.Index..open] + code;
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
                found.Add($"{relative}|new HttpClient({Read(text, m.Index + m.Length - 1).FirstArgument})");
            }
        }

        return found;
    }

    /// <summary>
    /// The statement hands <c>OutboundHttpHandler.Create</c> to <c>ConfigurePrimaryHttpMessageHandler</c>,
    /// so the guard is the handler that connects, and assigns a <c>Timeout</c>. Either appearing
    /// somewhere in the statement is not enough: a guard built and dropped, or a handler added
    /// beside an unguarded primary one, would still contain the text.
    /// </summary>
    internal static bool IsGuarded(string statement)
    {
        var at = statement.IndexOf("ConfigurePrimaryHttpMessageHandler", StringComparison.Ordinal);
        if (at < 0)
        {
            return false;
        }

        var open = statement.IndexOf('(', at);
        if (open < 0)
        {
            return false;
        }

        var (primary, _, _) = Read(statement, open);
        return primary.Contains("OutboundHttpHandler.Create(", StringComparison.Ordinal)
            && TimeoutAssignment.IsMatch(statement);
    }

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
        var sources = ProductionSources().ToList();
        sources.Should().NotBeEmpty("an empty scan would pass for any code");
        sources.Select(s => s.Relative).Should().Contain(
            "barakoCMS/Extensions/ServiceCollectionExtensions.cs", "the scan has to be reading the production sources");

        var all = DirectClients();

        all.Where(k => !DirectHttpClients.ContainsKey(k)).Should().BeEmpty(
            "a client built by hand skips the factory and the guard; register one with AddHttpClient or name this one with its reason");
        DirectHttpClients.Keys.Where(k => !all.Contains(k)).Should().BeEmpty();
    }

    private static readonly HashSet<string> UnnamedClientFiles = new()
    {
        "BarakoCMS.ExternalAuth/FacebookAuthEndpoints.cs",
        "BarakoCMS.ExternalAuth/GitHubAuthEndpoints.cs",
        "BarakoCMS.ExternalAuth/GoogleAuthEndpoints.cs",
        "BarakoCMS.ExternalAuth/LinkedInAuthEndpoints.cs",
    };

    /// <summary>
    /// A literal in a <c>CreateClient</c> call is a copy of a name. Rename the registration and the
    /// copy keeps working, on the unguarded default client. Callers pass a constant.
    /// </summary>
    [Fact]
    public void Every_CreateClient_call_passes_a_name_constant_and_the_unnamed_ones_are_the_provider_endpoints()
    {
        var literals = new List<string>();
        var unnamedOutsideTheList = new List<string>();
        var copies = new List<string>();
        var seen = 0;
        foreach (var (relative, text) in ProductionSources())
        {
            // A constant elsewhere that repeats the name is the same copy, one step removed.
            if (relative != "barakoCMS/Infrastructure/Http/HttpClientNames.cs"
                && text.Contains("\"ExternalApi\"", StringComparison.Ordinal))
            {
                copies.Add(relative);
            }

            foreach (Match m in CreateClientCall.Matches(text))
            {
                seen++;
                var (first, _, _) = Read(text, m.Index + m.Length - 1);
                if (first.Contains('"'))
                {
                    literals.Add($"{relative}|CreateClient({first})");
                }
                else if (first.Length == 0 && !UnnamedClientFiles.Contains(relative))
                {
                    unnamedOutsideTheList.Add(relative);
                }
            }
        }

        seen.Should().BeGreaterThan(5, "the scan has to find the callers or it proves nothing");
        literals.Should().BeEmpty("pass HttpClientNames.ExternalApi, not its text");
        copies.Should().BeEmpty("use HttpClientNames.ExternalApi rather than a second spelling of the name");
        unnamedOutsideTheList.Should().BeEmpty(
            "the unnamed client is unguarded; only the fixed-address provider endpoints may ask for it");
    }

    // The patterns above are what the scan stands on, so they are pinned against sample source.

    [Theory]
    [InlineData("services.AddHttpClient();", true)]
    [InlineData("services.AddHttpClient<IFoo, Foo>();", true)]
    [InlineData("services.AddHttpClient<IFoo<Bar>, Baz>();", true)]
    [InlineData("services.AddHttpClient<IFoo<Bar<Qux>>, Baz>(x => x.Timeout = t);", true)]
    [InlineData("services\n    .AddHttpClient(\"a\");", true)]
    [InlineData("services.\n    AddHttpClient(\"a\");", true)]
    [InlineData("services. AddHttpClient<IFoo<Bar>, Baz>(\n);", true)]
    [InlineData("services.AddHttpClientInstrumentation();", false)]
    [InlineData("// services.AddHttpClient();", false)]
    [InlineData("/* services.AddHttpClient(); */", false)]
    [InlineData("/* one\n services.AddHttpClient<IFoo<Bar>, Baz>();\n two */", false)]
    [InlineData("var s = \"/* \"; services.AddHttpClient(); var t = \" */\";", true)]
    public void The_registration_pattern_matches_the_forms_it_has_to(string source, bool matches)
    {
        Registration.IsMatch(WithoutComments(source)).Should().Be(matches);
    }

    [Theory]
    [InlineData("var c = new HttpClient();", true)]
    [InlineData("var c = new HttpClient(handler);", true)]
    [InlineData("var c = new HttpClient { Timeout = t };", true)]
    [InlineData("var c = new HttpClient\n{\n    Timeout = t,\n};", true)]
    [InlineData("var c = new System.Net.Http.HttpClient(h);", true)]
    [InlineData("var c = new global::System.Net.Http.HttpClient {};", true)]
    [InlineData("HttpClient c = new();", true)]
    [InlineData("private readonly HttpClient _c = new();", true)]
    [InlineData("var c = new HttpClientHandler();", false)]
    [InlineData("// var c = new HttpClient();", false)]
    [InlineData("/* var c = new HttpClient { }; */", false)]
    public void The_direct_construction_pattern_matches_the_forms_it_has_to(string source, bool matches)
    {
        Direct.IsMatch(WithoutComments(source)).Should().Be(matches);
    }

    [Fact]
    public void A_target_typed_new_returned_from_a_method_is_the_documented_blind_spot()
    {
        Direct.IsMatch(WithoutComments("HttpClient Make() => new();")).Should().BeFalse();
    }

    [Theory]
    [InlineData(
        "services.AddHttpClient(N).ConfigurePrimaryHttpMessageHandler(sp => OutboundHttpHandler.Create(g, p)).ConfigureHttpClient(c => c.Timeout = t);",
        true)]
    [InlineData(
        "services.AddHttpClient(N).ConfigurePrimaryHttpMessageHandler(sp => OutboundHttpHandler.Create(g, p)).ConfigureHttpClient(c => Timeout = t);",
        true)]
    [InlineData(
        "services.AddHttpClient(N).ConfigurePrimaryHttpMessageHandler(sp => OutboundHttpHandler.Create(g, p));",
        false)]
    [InlineData(
        "services.AddHttpClient(N).ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler()).AddHttpMessageHandler(sp => OutboundHttpHandler.Create(g, p)).ConfigureHttpClient(c => c.Timeout = t);",
        false)]
    [InlineData(
        "services.AddHttpClient(N).ConfigurePrimaryHttpMessageHandler(sp => OutboundHttpHandler.Create(g, p)).ConfigureHttpClient(c => c.Timeout == t);",
        false)]
    [InlineData(
        "services.AddHttpClient(N).ConfigurePrimaryHttpMessageHandler(sp => OutboundHttpHandler.Create(g, p)).ConfigureHttpClient(c => c.AttemptTimeout = t);",
        false)]
    [InlineData("services.AddHttpClient(N).ConfigureHttpClient(c => c.Timeout = t);", false)]
    public void The_guard_check_wants_the_primary_handler_and_a_timeout_assignment(string statement, bool guarded)
    {
        IsGuarded(statement).Should().Be(guarded);
    }
}
