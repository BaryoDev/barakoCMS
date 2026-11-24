using System.Text.RegularExpressions;
using barakoCMS.Infrastructure.Caching;
using barakoCMS.Infrastructure.Multitenancy;
using BarakoCMS.AI;
using BarakoCMS.Diagnostics.Forwarding;
using BarakoCMS.Email.Smtp;
using BarakoCMS.Files.S3;
using BarakoCMS.Pages;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// docs/configuration.md lists every configuration key the shipped source reads, and nothing else.
/// A key read with no row fails the first test, a row nothing reads fails the second.
/// </summary>
/// <remarks>
/// Source scans, so they need no database or host. The scan is <see cref="ConfigurationKeyScanner"/>.
/// It sees a key written as a literal, a constant, a concatenation or an interpolated string. Three
/// things it cannot see are listed here, each with its reason: options classes bound as a whole
/// (expanded by reflection, so a new property needs a row), keys built at run time (listed in
/// <see cref="RunTimeKeys"/>, and a row under one of those is checked only against the file that
/// builds it), and reads whose key is wholly a variable (<see cref="VariableKeyFiles"/>).
/// Keys compare case-insensitively, the way configuration does, with ":" as the separator.
/// </remarks>
public class ConfigurationReferenceTests
{
    private const string Page = "docs/configuration.md";

    // Options classes bound as a whole from a section. The test expands each to its public
    // settable properties, so adding a property without a row fails.
    private static readonly (string Section, Type Options)[] BoundOptions =
    [
        ("Delivery:ResponseCache", typeof(DeliveryResponseCacheOptions)),
        ("Multitenancy", typeof(MultitenancyOptions)),
        ("Modules:AI", typeof(AiOptions)),
        ("Modules:Pages", typeof(PagesOptions)),
        ("Modules:Email.Smtp", typeof(SmtpOptions)),
        ("Modules:Files.S3", typeof(S3StorageOptions)),
        ("Modules:Diagnostics:Forwarding", typeof(ForwardingOptions)),
    ];

    // Key patterns the code builds at run time, or reads one leaf at a time from a helper. "*" is any
    // text. Each names the file that builds them and a word that file must contain, so an entry
    // outlives neither its file nor its code. At least one row has to match each pattern. A row that
    // matches is checked against this entry only, so a new key under one of these prefixes needs a
    // row by hand; the reason says where to look.
    internal static readonly (string Pattern, string File, string Token, string Reason)[] RunTimeKeys =
    [
        ("Kestrel:Limits:*", "barakoCMS/Extensions/ServiceCollectionExtensions.cs", "Limits",
            "bound onto KestrelServerLimits one child at a time, so any property of that class is a key; the rows list the ones that matter"),
        ("RateLimiting:*", "barakoCMS/Infrastructure/Security/RateLimitSetup.cs", "RateLimiting",
            "one section per window, each with PermitLimit, WindowSeconds and QueueLimit, read through a helper that takes the window name"),
        ("Delivery:CdnPurge:*", "barakoCMS/Infrastructure/Caching/Purge/CdnPurgeOptions.cs", "CdnPurge",
            "read through a helper that takes the section name"),
        ("Delivery:CacheTags:*", "barakoCMS/Infrastructure/Caching/DeliveryTagBounds.cs", "CacheTags",
            "read through a helper that takes the key"),
        ("Workflows:Email:Attachments:*", "barakoCMS/Features/Workflows/Actions/EmailAttachments.cs", "Attachments",
            "read through a helper that takes the key"),
        ("Workflows:Outbound:*", "barakoCMS/Infrastructure/Http/OutboundResilienceOptions.cs", "Outbound",
            "read through a helper that takes the key"),
        ("ForwardedHeaders:*", "barakoCMS/Infrastructure/Security/ForwardedHeadersSetup.cs", "ForwardedHeaders",
            "read from the section one leaf at a time"),
        ("Hsts:*", "barakoCMS/Infrastructure/Security/HstsPolicy.cs", "Hsts",
            "read from the section one leaf at a time"),
        ("Oidc:Providers:*", "BarakoCMS.ExternalAuth/OidcProviders.cs", "Providers",
            "one section per provider, named by the operator"),
        ("Secrets:Keys:*", "barakoCMS/Infrastructure/Security/KeyRing.cs", "Keys",
            "a key ring: one entry per key id, named by the operator"),
        ("Mfa:Keys:*", "barakoCMS/Infrastructure/Security/KeyRing.cs", "Keys",
            "a key ring: one entry per key id, named by the operator"),
        ("Connectors:Keys:*", "barakoCMS/Infrastructure/Security/KeyRing.cs", "Keys",
            "a key ring: one entry per key id, named by the operator"),
        ("Secrets:CurrentKey", "barakoCMS/Infrastructure/Security/KeyRing.cs", "CurrentKey",
            "read as {section}:CurrentKey for each key ring"),
        ("Mfa:CurrentKey", "barakoCMS/Infrastructure/Security/KeyRing.cs", "CurrentKey",
            "read as {section}:CurrentKey for each key ring"),
        ("Connectors:CurrentKey", "barakoCMS/Infrastructure/Security/KeyRing.cs", "CurrentKey",
            "read as {section}:CurrentKey for each key ring"),
        ("Facebook:Enabled", "BarakoCMS.ExternalAuth/ExternalAuthSupport.cs", "Enabled",
            "read as {section}:Enabled for each provider's own section"),
        ("GitHub:Enabled", "BarakoCMS.ExternalAuth/ExternalAuthSupport.cs", "Enabled",
            "read as {section}:Enabled for each provider's own section"),
        ("Google:Enabled", "BarakoCMS.ExternalAuth/ExternalAuthSupport.cs", "Enabled",
            "read as {section}:Enabled for each provider's own section"),
        ("LinkedIn:Enabled", "BarakoCMS.ExternalAuth/ExternalAuthSupport.cs", "Enabled",
            "read as {section}:Enabled for each provider's own section"),
        ("Oidc:RateLimit:*","BarakoCMS.ExternalAuth/OidcSupport.cs", "RateLimit",
            "read as {prefix}:PermitLimit and {prefix}:WindowSeconds"),
        ("Social:RateLimit:*", "BarakoCMS.ExternalAuth/SocialRateLimit.cs", "RateLimit",
            "read as {prefix}:PermitLimit and the three keys beside it"),
    ];

    // Files where a read passes a variable as the whole key. Each reads keys this page already lists
    // under another heading, or a section named by the caller.
    internal static readonly Dictionary<string, string> VariableKeyFiles = new()
    {
        ["BarakoCMS.ExternalAuth/ExternalAuthSupport.cs"] = "reads a provider's client id and secret from the provider's own section",
        ["BarakoCMS.Files.S3/S3FilesModule.cs"] = "lists which of the S3 options are set, by property name; all are bound options listed under Files.S3",
        ["barakoCMS/Extensions/ServiceCollectionExtensions.cs"] = "builds Modules:{name} and the legacy section from a module's name; the module sections are listed per module",
        ["barakoCMS/Features/Modules/Configuration/ModuleConfigurationReader.cs"] = "reads a module's own section to show it in the console; keys are the module rows",
        ["barakoCMS/Features/Workflows/Actions/EmailAttachments.cs"] = "Workflows:Email:Attachments, one helper for three keys",
        ["barakoCMS/Infrastructure/Caching/DeliveryTagBounds.cs"] = "Delivery:CacheTags, one helper for two keys",
        ["barakoCMS/Infrastructure/Caching/Purge/CdnPurgeOptions.cs"] = "Delivery:CdnPurge, one helper over a section name",
        ["barakoCMS/Infrastructure/Connectors/ConnectorOptions.cs"] = "compares the other key material in use with Connectors:Key",
        ["barakoCMS/Infrastructure/Http/OutboundResilienceOptions.cs"] = "Workflows:Outbound, one helper for every key",
        ["barakoCMS/Infrastructure/Security/CanonicalHost.cs"] = "one helper that reads a key it is handed from this file",
        ["barakoCMS/Infrastructure/Security/ForwardedHeadersSetup.cs"] = "ForwardedHeaders, one helper for the list keys",
        ["barakoCMS/Infrastructure/Security/KeyRing.cs"] = "the Secrets, Mfa and Connectors key rings, which differ only in the section name",
        ["barakoCMS/Infrastructure/Security/RateLimitSetup.cs"] = "RateLimiting, one helper per window name",
        ["barakoCMS/Infrastructure/Services/ConfigurationService.cs"] = "the console's settings reader, which takes the key from the caller",
        ["barakoCMS/Infrastructure/Services/TenantSettingsResolver.cs"] = "reads the host default a module declares for a tenant setting; those are the module rows",
    };

    // Files where the receiver of every read is the module's own section, not the root.
    private static readonly HashSet<string> ModuleSectionFiles = new(StringComparer.Ordinal)
    {
        "BarakoCMS.Diagnostics/DiagnosticsModule.cs",
    };

    private sealed record Row(string Key, string Env, int Line, string[] Cells);

    [Fact]
    public void Every_key_the_source_reads_is_on_the_page()
    {
        var scan = Scan();
        var rows = PageRows();
        var keys = rows.Select(r => r.Key).ToList();

        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var read in scan.Reads)
        {
            var found = read.Kind switch
            {
                ConfigurationKeyScanner.ReadKind.Key or ConfigurationKeyScanner.ReadKind.Env when read.Key.Contains('*') =>
                    keys.Any(k => Glob(read.Key, k)),
                ConfigurationKeyScanner.ReadKind.Key or ConfigurationKeyScanner.ReadKind.Env =>
                    keys.Any(k => string.Equals(k, read.Key, StringComparison.OrdinalIgnoreCase)),
                ConfigurationKeyScanner.ReadKind.Prefix =>
                    keys.Any(k => string.Equals(k, read.Key, StringComparison.OrdinalIgnoreCase)
                        || k.StartsWith(read.Key + ":", StringComparison.OrdinalIgnoreCase)),
                _ => keys.Any(k => (":" + k + ":").Contains(":" + read.Key + ":", StringComparison.OrdinalIgnoreCase)),
            };

            if (!found)
                missing.Add($"{read.Key} ({read.Kind}, read at {read.File}:{read.Line})");
        }

        foreach (var (section, options) in BoundOptions)
            foreach (var key in ConfigurationKeyScanner.ExpandBound(section, options))
                if (!keys.Contains(key, StringComparer.OrdinalIgnoreCase))
                    missing.Add($"{key} (a property of {options.Name})");

        Assert.True(missing.Count == 0,
            $"{Page} has no row for {missing.Count} key(s) the source reads. Add a row for each:\n  "
            + string.Join("\n  ", missing));
    }

    [Fact]
    public void Every_key_on_the_page_is_read_by_the_source()
    {
        var scan = Scan();
        var rows = PageRows();
        var bound = BoundOptions
            .SelectMany(b => ConfigurationKeyScanner.ExpandBound(b.Section, b.Options))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unread = new List<string>();
        foreach (var row in rows)
        {
            var read =
                scan.Reads.Any(r => r.Kind is ConfigurationKeyScanner.ReadKind.Key or ConfigurationKeyScanner.ReadKind.Env
                    && (string.Equals(r.Key, row.Key, StringComparison.OrdinalIgnoreCase) || (r.Key.Contains('*') && Glob(r.Key, row.Key))))
                || scan.Reads.Any(r => r.Kind == ConfigurationKeyScanner.ReadKind.Prefix
                    && (string.Equals(r.Key, row.Key, StringComparison.OrdinalIgnoreCase)
                        || row.Key.StartsWith(r.Key + ":", StringComparison.OrdinalIgnoreCase)))
                || scan.Reads.Any(r => r.Kind == ConfigurationKeyScanner.ReadKind.Leaf
                    && (":" + row.Key + ":").Contains(":" + r.Key + ":", StringComparison.OrdinalIgnoreCase))
                || bound.Contains(row.Key)
                || RunTimeKeys.Any(e => Glob(e.Pattern, row.Key));

            if (!read)
                unread.Add($"{row.Key} (line {row.Line})");
        }

        Assert.True(unread.Count == 0,
            $"{Page} lists {unread.Count} key(s) that nothing in the source reads. Remove or rename each:\n  "
            + string.Join("\n  ", unread));
    }

    [Fact]
    public void Run_time_key_entries_still_match_the_code_and_a_row()
    {
        var root = RepoRoot();
        var keys = PageRows().Select(r => r.Key).ToList();
        var stale = new List<string>();
        foreach (var e in RunTimeKeys)
        {
            var path = Path.Combine(root, e.File);
            if (!File.Exists(path))
                stale.Add($"{e.Pattern}: {e.File} does not exist");
            else if (!File.ReadAllText(path).Contains(e.Token, StringComparison.Ordinal))
                stale.Add($"{e.Pattern}: {e.File} no longer contains \"{e.Token}\"");

            if (!keys.Any(k => Glob(e.Pattern, k)))
                stale.Add($"{e.Pattern}: no row on the page matches it");
        }

        Assert.True(stale.Count == 0, "RunTimeKeys has stale entries:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void Reads_with_a_variable_key_are_only_in_the_listed_files()
    {
        var scan = Scan();
        var unexpected = scan.Unresolved
            .Where(u => !VariableKeyFiles.ContainsKey(u.File))
            .Select(u => $"{u.File}:{u.Line}  key written as {u.Expression}")
            .ToList();

        Assert.True(unexpected.Count == 0,
            "These reads pass a key the scan cannot resolve. Write the key as a literal or a constant, "
            + "or add the file to VariableKeyFiles with the reason and give its keys a row on the page:\n  "
            + string.Join("\n  ", unexpected));

        var gone = VariableKeyFiles.Keys.Where(f => scan.Unresolved.All(u => u.File != f)).ToList();
        Assert.True(gone.Count == 0,
            "VariableKeyFiles lists files with no variable-key read any more. Remove them:\n  " + string.Join("\n  ", gone));
    }

    [Fact]
    public void Env_column_is_the_key_with_double_underscores()
    {
        var wrong = PageRows()
            .Where(r => r.Env != r.Key.Replace(":", "__", StringComparison.Ordinal))
            .Select(r => $"{r.Key} (line {r.Line}) has env var {r.Env}, expected {r.Key.Replace(":", "__", StringComparison.Ordinal)}")
            .ToList();

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void Every_row_fills_every_column_and_keys_are_unique()
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in PageRows())
        {
            if (r.Cells.Length < 5 || r.Cells.Any(string.IsNullOrWhiteSpace))
                problems.Add($"{r.Key} (line {r.Line}) needs the five columns: key, env var, default, allowed, blank or malformed");
            if (!seen.Add(r.Key))
                problems.Add($"{r.Key} (line {r.Line}) is listed twice");
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Every_module_has_a_section_on_the_page()
    {
        var root = RepoRoot();
        var page = File.ReadAllText(Path.Combine(root, Page));
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in ConfigurationKeyScanner.ShippedFiles(root))
        {
            var text = File.ReadAllText(Path.Combine(root, file));
            if (!Regex.IsMatch(text, @"class\s+\w+\s*(?:\([^)]*\))?\s*:\s*[^{;]*\bIBarakoModule\b"))
                continue;
            foreach (Match m in Regex.Matches(text, @"\bstring\s+Name\s*=>\s*""(?<n>[^""]+)"""))
                names.Add(m.Groups["n"].Value);
        }

        Assert.NotEmpty(names);
        var absent = names.Where(n => !Regex.IsMatch(page, @"^## Module: " + Regex.Escape(n) + @"\s*$", RegexOptions.Multiline)).ToList();
        Assert.True(absent.Count == 0, $"{Page} needs a \"## Module: <name>\" heading for: " + string.Join(", ", absent));
    }

    [Fact]
    public void The_readme_links_the_page()
    {
        var readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));
        Assert.Contains("(docs/configuration.md)", readme);
    }

    private static ConfigurationKeyScanner.Result Scan() =>
        ConfigurationKeyScanner.Scan(RepoRoot(), ModuleSectionFiles);

    private static List<Row> PageRows()
    {
        var lines = File.ReadAllLines(Path.Combine(RepoRoot(), Page));
        var rows = new List<Row>();
        for (var i = 0; i < lines.Length; i++)
        {
            var m = Regex.Match(lines[i], @"^\|\s*`(?<k>[^`]+)`\s*\|\s*`(?<e>[^`]+)`\s*\|");
            if (!m.Success)
                continue;
            var cells = lines[i].Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            rows.Add(new Row(m.Groups["k"].Value, m.Groups["e"].Value, i + 1, cells));
        }

        Assert.NotEmpty(rows);
        return rows;
    }

    private static bool Glob(string pattern, string text) =>
        Regex.IsMatch(text, "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "barakoCMS.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("barakoCMS.sln not found above " + AppContext.BaseDirectory);
    }
}
