using System.Globalization;
using System.Text.RegularExpressions;
using barakoCMS.Infrastructure.Security;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// docs/troubleshooting.md quotes the messages the API (and the compose files) refuse to start with.
/// A message the code no longer says, or a guard the page does not name, is a page that sends a
/// reader after a fix for something else. Like <see cref="ConfigurationReferenceTests"/>, this reads
/// source text only: no host, no database.
/// </summary>
public sealed class TroubleshootingPageTests
{
    private const string Page = "docs/troubleshooting.md";
    private const string SecurityFolder = "barakoCMS/Infrastructure/Security";

    // A source file that holds a throw but cannot stop a start. Each has the reason, so a new file
    // that throws has to be put here with a reason or on the page.
    private static readonly Dictionary<string, string> NotAStartFailure = new(StringComparer.Ordinal)
    {
        ["AesGcmEnvelope.cs"] = "throws on a malformed stored value, when one is read",
        ["CanonicalHost.cs"] = "throws when a link is built (a request), not at start",
        ["CanonicalPublicBaseUrl.cs"] = "throws when a link is built (a request) and the base URL is not set",
        ["SecretProtector.cs"] = "throws only when no key exists at all, which JwtKeyGuard has already refused",
    };

    // The guard classes the page names. The file name is the class name.
    private static readonly string[] OnThePage =
    [
        "DatabasePasswordGuard.cs",
        "DatabaseRoleGuard.cs",
        "ForwardedHeadersSetup.cs",
        "HostFilteringGuard.cs",
        "HstsPolicy.cs",
        "InitialAdminGuard.cs",
        "JwtKeyGuard.cs",
        "KeyRing.cs",
        "RateLimitSetup.cs",
        "SecretFiles.cs",
    ];

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    // Interpolation holes whose value is a constant, so the page can quote the text a reader sees.
    // Keyed by file name and the text between the braces. A number comes from the constant itself,
    // so changing the constant fails the quote until the page says the new number.
    private static readonly Dictionary<(string File, string Expression), string> Holes = new()
    {
        [("JwtKeyGuard.cs", "MinLength")] = N(JwtKeyGuard.MinLength),
        [("InitialAdminGuard.cs", "MinLength")] = N(InitialAdminGuard.MinLength),
        [("HostFilteringGuard.cs", "CanonicalHost.AllowedHostsKey")] = "AllowedHosts",
        [("HostFilteringGuard.cs", "AllowAnyHostKey")] = "Security:AllowAnyHost",
        [("DatabaseRoleGuard.cs", "AllowKey")] = "Database:AllowSuperuser",
        [("DatabaseRoleGuard.cs", "AllowKey.Replace(\":\", \"__\")")] = "Database__AllowSuperuser",
        [("ForwardedHeadersSetup.cs", "Section")] = "ForwardedHeaders",
        [("HstsPolicy.cs", "Section")] = "Hsts",
        [("HstsPolicy.cs", "DefaultMaxAgeDays")] = N(HstsPolicy.DefaultMaxAgeDays),
        [("RateLimitSetup.cs", "Section")] = "RateLimiting",
        [("RateLimitSetup.cs", "RendererKeyMinLength")] = N(RateLimitSetup.RendererKeyMinLength),
        [("RateLimitSetup.cs", "nameof(RateLimitWindow.PermitLimit)")] = "PermitLimit",
        [("RateLimitSetup.cs", "nameof(RateLimitPartitionBy.Ip)")] = "Ip",
        [("RateLimitSetup.cs", "nameof(RateLimitPartitionBy.User)")] = "User",
        [("RateLimitSetup.cs", "nameof(RateLimitPartitionBy.ApiKey)")] = "ApiKey",
        [("KeyRing.cs", "MinimumKeyLength")] = N(KeyRing.MinimumKeyLength),
        [("EmailVerificationOptions.cs", "RequiredKey")] = "Auth:RequireEmailVerification",
        [("EmailVerificationOptions.cs", "AcknowledgeKey")] = "Auth:AcknowledgeUnverifiedRegistration",
        [("TenancyOptions.cs", "ModeKey")] = "Tenancy:Mode",
    };

    private sealed record Quote(string Source, string Line, int PageLine);

    [Fact]
    public void Every_message_the_page_quotes_is_still_in_the_source()
    {
        var root = RepoRoot();
        var quotes = Quotes(root);
        Assert.NotEmpty(quotes);

        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var quote in quotes)
        {
            var path = Path.Combine(root, quote.Source);
            if (!File.Exists(path))
            {
                problems.Add($"{Page}:{quote.PageLine} names {quote.Source}, which does not exist");
                continue;
            }

            if (!texts.TryGetValue(quote.Source, out var text))
                texts[quote.Source] = text = SearchableText(quote.Source, File.ReadAllText(path));

            var parts = Normalise(quote.Line)
                .Split("...", StringSplitOptions.None)
                .Select(p => p.Trim())
                .Where(p => p.Length > 0)
                .ToList();

            if (parts.Count == 0 || parts.All(p => p.Length < 12))
            {
                problems.Add($"{Page}:{quote.PageLine} quotes too little to prove anything: \"{quote.Line}\"");
                continue;
            }

            foreach (var part in parts)
                if (!text.Contains(part, StringComparison.Ordinal))
                    problems.Add($"{Page}:{quote.PageLine} quotes \"{part}\", which is not in {quote.Source}");
        }

        Assert.True(problems.Count == 0,
            "A message on the page no longer matches the code. Update the page, or the quote, to what the code says:\n  "
            + string.Join("\n  ", problems));
    }

    // The page also states some of these numbers in prose, outside any quoted block. Each must say
    // what the constant says.
    [Fact]
    public void A_number_the_page_states_in_prose_is_the_one_the_code_uses()
    {
        var page = Normalise(File.ReadAllText(Path.Combine(RepoRoot(), Page)));
        var jwt = N(JwtKeyGuard.MinLength);
        var admin = N(InitialAdminGuard.MinLength);
        var keyRing = N(KeyRing.MinimumKeyLength);
        var renderer = N(RateLimitSetup.RendererKeyMinLength);

        string[] expected =
        [
            $"unset or under {jwt} characters",
            $"random value of {jwt} characters or more, for example",
            $"it is shorter than {admin} characters",
            $"random value of {admin} characters or more.",
            $"a value of {keyRing} characters or more that is not the JWT key",
            $"a secret of {renderer} characters or more, or absent",
        ];

        var missing = expected.Where(e => !page.Contains(Normalise(e), StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0,
            $"{Page} states a length that no longer matches the code. Update the page to the constant's value:\n  "
            + string.Join("\n  ", missing));
    }

    // File level: a guard class has to be named, and has to still throw. This does not prove that
    // each refusal inside it is on the page; the page lists the common ones.
    [Fact]
    public void Every_guard_that_can_stop_a_start_is_named_on_the_page()
    {
        var root = RepoRoot();
        var page = File.ReadAllText(Path.Combine(root, Page));
        var folder = Path.Combine(root, SecurityFolder.Replace('/', Path.DirectorySeparatorChar));

        var throwing = Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(f => Throws.IsMatch(ConfigurationKeyScanner.StripComments(File.ReadAllText(f))))
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        var unclassified = throwing
            .Where(f => !OnThePage.Contains(f) && !NotAStartFailure.ContainsKey(f))
            .ToList();
        Assert.True(unclassified.Count == 0,
            $"{SecurityFolder} has a file that throws or exits and is neither named on {Page} nor listed here with a reason. "
            + "Add its message to the page and to OnThePage, or to NotAStartFailure with why it cannot stop a start:\n  "
            + string.Join("\n  ", unclassified));

        var notNamed = OnThePage
            .Where(f => !Regex.IsMatch(page, @"\b" + Regex.Escape(Path.GetFileNameWithoutExtension(f)) + @"\b"))
            .ToList();
        Assert.True(notNamed.Count == 0,
            $"{Page} does not name: " + string.Join(", ", notNamed.Select(f => Path.GetFileNameWithoutExtension(f))));

        var stale = OnThePage.Concat(NotAStartFailure.Keys).Where(f => !throwing.Contains(f)).ToList();
        Assert.True(stale.Count == 0,
            "These files no longer throw or exit, so they do not belong in OnThePage or NotAStartFailure: "
            + string.Join(", ", stale));
    }

    private static readonly Regex Throws =
        new(@"\bthrow\b|\bEnvironment\.Exit\b", RegexOptions.CultureInvariant);

    private static readonly Regex Fence = new(@"^```(?<info>[A-Za-z]*)\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex SourceComment = new(@"^<!--\s*source:\s*(?<path>\S+)\s*-->$", RegexOptions.CultureInvariant);

    // Every line of every ```text block, with the file named by the "source" comment above it. A
    // text block with no comment is an error: it would be a quote nothing checks.
    private static List<Quote> Quotes(string root)
    {
        var lines = File.ReadAllLines(Path.Combine(root, Page));
        var quotes = new List<Quote>();
        string? source = null;
        var inBlock = false;
        var checking = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var fence = Fence.Match(line);

            if (!inBlock)
            {
                var comment = SourceComment.Match(line);
                if (comment.Success)
                    source = comment.Groups["path"].Value;

                if (fence.Success)
                {
                    inBlock = true;
                    checking = fence.Groups["info"].Value == "text";
                    if (checking && source is null)
                        throw new InvalidOperationException($"{Page}:{i + 1} opens a text block with no <!-- source: path --> comment above it.");
                }

                continue;
            }

            if (fence.Success)
            {
                inBlock = false;
                if (checking) source = null;
                continue;
            }

            if (checking && line.Trim().Length > 0)
                quotes.Add(new Quote(source!, line, i + 1));
        }

        return quotes;
    }

    // The text a user would read: comments gone, adjacent literals joined, constant holes filled in,
    // escapes undone, runs of white space squeezed. A yml file is searched as it is, minus its
    // comment lines, so a comment cannot stand in for the text a reader sees.
    private static string SearchableText(string relativePath, string raw)
    {
        if (!relativePath.EndsWith(".cs", StringComparison.Ordinal))
            return Normalise(string.Join('\n', raw.Split('\n').Where(l => !l.TrimStart().StartsWith('#'))));

        var file = Path.GetFileName(relativePath);
        var text = ConfigurationKeyScanner.StripComments(raw);

        text = Regex.Replace(text, "\"\\s*\\+\\s*\\$?\"", string.Empty);
        text = Regex.Replace(text, @"\{(?<expr>[^{}]+)\}", m =>
            Holes.TryGetValue((file, m.Groups["expr"].Value), out var value) ? value : m.Value);
        text = text.Replace("\\\"", "\"").Replace("\\\\", "\\");

        return Normalise(text);
    }

    private static string Normalise(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "barakoCMS.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("barakoCMS.sln not found above " + AppContext.BaseDirectory);
    }
}
