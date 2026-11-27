using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The CLAUDE.md section 1 and 1a rules that can be stated about types and references, held by the
/// build instead of by whoever reads the prose.
/// </summary>
/// <remarks>
/// The scan covers the core assembly only. Module assemblies are not scanned: the test project
/// references them, but several of their endpoints are public or sit outside Features/, so scanning
/// them is a decision with its own allowlists, not a side effect of this file.
///
/// Each rule first asserts the scan found what Features/ holds.
/// A filter that matches nothing passes every rule below, which is the coincidental pass section 4
/// warns about. Allowlist entries carry their reason, and a test fails when one names a type that
/// no longer exists, so the lists cannot outlive what they excuse.
/// </remarks>
public class ArchitectureTests
{
    private static readonly Assembly Core = typeof(barakoCMS.Data.DataSeeder).Assembly;

    private const string FeaturesPrefix = "barakoCMS.Features.";

    // Unbounded list endpoints today, each with why it is allowed. Bounding one that returns data a
    // tenant can grow changes its response shape, an HTTP break under section 6, so the owner
    // decides (#1020). Those are marked "grows".
    internal static readonly Dictionary<string, string> UnboundedListEndpoints = new()
    {
        ["barakoCMS.Features.ContentType.Blueprints.List.Endpoint"] =
            "returns the blueprints shipped in the assembly, a fixed set defined in code",
        ["barakoCMS.Features.Stations.ListStationsEndpoint"] =
            "grows: a tenant's station keys, created by its admins; behind manage_stations",
        ["barakoCMS.Features.Content.Types.ListEndpoint"] =
            "grows: a tenant's content type definitions plus the account's shared ones, created by admins; names only, filtered to types the caller may read",
    };

    // Features whose endpoints sit directly in barakoCMS.Features.<Feature> rather than in an action
    // namespace below it. They predate the slice layout, and moving them is a refactor this test does
    // not make (#1020). The list only shrinks: a new feature starts as Features/<Feature>/<Action>/.
    internal static readonly Dictionary<string, string> FlatFeatureNamespaces = FlatFeatures();

    private static Dictionary<string, string> FlatFeatures()
    {
        const string reason = "predates the Features/<Feature>/<Action>/ layout; several endpoints share one file";
        string[] features =
        [
            "ApiKeys", "Changes", "Collections", "Connectors", "Me", "Numbering", "Preview", "Public",
            "Queries", "Redirects", "Requests", "Seo", "Settings", "Stations", "Tenants",
            "WebhookDeliveries", "WorkflowRuns", "Workflows",
        ];
        return features.ToDictionary(f => FeaturesPrefix + f, _ => reason);
    }

    // Types named *Repository allowed to exist, each with why.
    internal static readonly Dictionary<string, string> AllowedRepositories = new()
    {
        ["barakoCMS.Repository.IUserRepository"] =
            "Login and Register are built on it and unit tests mock it to check password rules; removing it is an auth-path refactor",
        ["barakoCMS.Repository.MartenUserRepository"] =
            "the implementation of IUserRepository, which stays for the same reason",
    };

    private static Type[] CoreTypes() => Core.GetTypes();

    internal static bool IsEndpoint(Type type) =>
        type is { IsClass: true, IsAbstract: false }
        && typeof(FastEndpoints.BaseEndpoint).IsAssignableFrom(type);

    private static Type[] FeatureEndpoints() => CoreTypes()
        .Where(IsEndpoint)
        .Where(t => t.FullName!.StartsWith(FeaturesPrefix, StringComparison.Ordinal))
        .ToArray();

    // The request a FastEndpoints endpoint binds, or null for EndpointWithoutRequest.
    internal static Type? RequestType(Type endpoint)
    {
        for (var t = endpoint.BaseType; t is not null; t = t.BaseType)
        {
            if (t.IsGenericType && t.Namespace == "FastEndpoints" && t.Name.StartsWith("Endpoint`", StringComparison.Ordinal))
            {
                return t.GetGenericArguments()[0];
            }
        }

        return null;
    }

    // A List action folder, or a List* endpoint in a flat feature namespace.
    private static bool IsListEndpoint(Type endpoint) =>
        endpoint.Namespace!.EndsWith(".List", StringComparison.Ordinal)
        || endpoint.Name.StartsWith("List", StringComparison.Ordinal);

    private static bool IsBounded(Type endpoint) =>
        RequestType(endpoint) is { } request && typeof(barakoCMS.Models.PaginatedRequest).IsAssignableFrom(request);

    [Fact]
    public void Only_the_workflow_email_action_sends_mail_on_a_tenants_behalf()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "barakoCMS")))
            root = root.Parent;
        root.Should().NotBeNull("the repository root is above the test output");

        var callers = Directory.EnumerateFiles(root!.FullName, "*.cs", SearchOption.AllDirectories)
            .Select(f => (Path: f, Parts: Path.GetRelativePath(root.FullName, f)
                .Split(Path.DirectorySeparatorChar)))
            .Where(f => f.Parts[0] is not (".claude" or "BarakoCMS.Tests")
                     && !f.Parts.Contains("obj") && !f.Parts.Contains("bin"))
            .Select(f => f.Path)
            .Where(f => File.ReadLines(f).Any(l =>
                l.Contains(".SendForTenantAsync(", StringComparison.Ordinal)
                && !l.TrimStart().StartsWith("//", StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .ToArray();

        callers.Should().NotBeEmpty("the scan has to find the one legitimate caller or it proves nothing");
        callers.Should().BeEquivalentTo(["EmailAction.cs"],
            "account mail (codes, verification, resets) must use SendEmailAsync, the host transport");
    }

    /// <summary>
    /// <c>JwtBearer.CreateToken</c> reads FastEndpoints' process-wide resolver, so a test that calls it
    /// is only right while no other host is starting or disposing. The host's <c>IJwtMinter</c> reads
    /// nothing global, and a test that needs a token takes it from there (#1408). No production file
    /// calls the old method, and the test files that still do are the serial ones, which run alone.
    /// </summary>
    [Fact]
    public void Only_serial_test_classes_call_JwtBearer_CreateToken()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "barakoCMS")))
            root = root.Parent;
        root.Should().NotBeNull("the repository root is above the test output");

        var serial = System.Text.Json.JsonDocument
            .Parse(File.ReadAllText(Path.Combine(root!.FullName, "scripts", "test-collections.json")))
            .RootElement.GetProperty("serial").EnumerateObject()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);
        serial.Should().NotBeEmpty("the serial list is what excuses a caller");

        // Whole-file text, so a call split across lines or spaced out is still found. Comments are
        // cut first, so a remark that names the method does not count.
        var call = new System.Text.RegularExpressions.Regex(@"JwtBearer\s*\.\s*CreateToken\s*\(");
        var comments = new System.Text.RegularExpressions.Regex(
            @"//[^\r\n]*|/\*.*?\*/", System.Text.RegularExpressions.RegexOptions.Singleline);
        var projects = Directory.EnumerateDirectories(root.FullName)
            .Where(d => Path.GetFileName(d) is var n && (n == "barakoCMS" || n.StartsWith("BarakoCMS.", StringComparison.Ordinal)))
            .ToArray();
        projects.Should().Contain(d => Path.GetFileName(d) == "BarakoCMS.Tests", "the scan has to reach the tests");

        var callers = projects
            .SelectMany(project => Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin")
                     && Path.GetFileName(f) != nameof(ArchitectureTests) + ".cs")
            .Where(f => call.IsMatch(comments.Replace(File.ReadAllText(f), "")))
            .Select(f => Path.GetRelativePath(Path.Combine(root.FullName, "BarakoCMS.Tests"), f).Replace('\\', '/'))
            .ToArray();

        callers.Should().Contain("JwtMinterGoldenTests.cs", "the scan has to find the golden test or it proves nothing");
        var production = callers.Where(c => c.StartsWith("../", StringComparison.Ordinal)
                                         && !c.StartsWith("../BarakoCMS.Testing/", StringComparison.Ordinal)).ToArray();
        production.Should().HaveCount(0, "the host signs through IJwtMinter, never JwtBearer.CreateToken");
        callers.Except(production).Where(c => !serial.Contains(c)).Should().HaveCount(0,
            "a test outside the serial list takes its token from the host's IJwtMinter, not from JwtBearer.CreateToken");
    }

    [Fact]
    public void The_scan_finds_the_endpoints_validators_and_list_endpoints_Features_holds()
    {
        FeatureEndpoints().Should().HaveCountGreaterThan(50,
            "Features/ holds well over 50 endpoints; fewer means the scan is reading the wrong assembly or filter");
        CoreTypes().Count(IsFluentValidator).Should().BeGreaterThan(20,
            "Features/ holds dozens of validators");
        FeatureEndpoints().Count(IsListEndpoint).Should().BeGreaterThanOrEqualTo(10,
            "Features/ holds ten List folders");
    }

    [Fact]
    public void The_core_references_no_module_assembly()
    {
        var referenced = Core.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        referenced.Should().Contain("BarakoCMS.Abstractions",
            "the core compiles against the contract, so a scan that misses it is reading the wrong list");

        referenced
            .Where(n => n.StartsWith("BarakoCMS.", StringComparison.OrdinalIgnoreCase) && n != "BarakoCMS.Abstractions")
            .Should().BeEmpty("modules depend on the core, never the reverse (CLAUDE.md section 1)");

        var csproj = Path.Combine(RepoPaths.Root, "barakoCMS", "barakoCMS.csproj");
        var projectReferences = XDocument.Load(csproj)
            .Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension((e.Attribute("Include")?.Value ?? string.Empty).Replace('\\', '/')))
            .ToArray();

        projectReferences.Should().Equal(["BarakoCMS.Abstractions"],
            "a project reference the code does not use yet still puts the module in the core's closure");
    }

    [Fact]
    public void No_type_is_an_MVC_controller()
    {
        var controllers = CoreTypes()
            .Where(t => InheritsFrom(t, "Microsoft.AspNetCore.Mvc.ControllerBase"))
            .Select(t => t.FullName)
            .ToArray();

        controllers.Should().BeEmpty("endpoints follow REPR through FastEndpoints; there are no controllers (CLAUDE.md section 1a)");
    }

    [Fact]
    public void No_feature_namespace_is_a_shared_Services_or_Handlers_folder()
    {
        var namespaces = CoreTypes()
            .Select(t => t.Namespace)
            .Where(n => n is not null && n.StartsWith(FeaturesPrefix, StringComparison.Ordinal))
            .Distinct()
            .ToArray();
        namespaces.Should().HaveCountGreaterThan(50, "Features/ holds dozens of slices");

        namespaces
            .Where(n => n!.Split('.').Any(segment => segment is "Services" or "Handlers"))
            .Should().BeEmpty("slices hold their own logic; a Services or Handlers folder every feature reaches into is what section 1a rules out");

        var root = Path.Combine(RepoPaths.Root, "barakoCMS", "Features");
        Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Where(d => Path.GetFileName(d) is "Services" or "Handlers")
            .Select(d => Path.GetRelativePath(root, d))
            .Should().BeEmpty("a folder can hold types in another namespace, so the folders are checked as well");
    }

    [Fact]
    public void No_type_is_named_Repository_outside_the_allowlist()
    {
        var repositories = CoreTypes()
            .Where(t => !IsCompilerGenerated(t) && t.Name.EndsWith("Repository", StringComparison.Ordinal))
            .Select(t => t.FullName!)
            .ToArray();
        repositories.Should().NotBeEmpty("UserRepository exists today, so a scan that finds nothing is broken");

        repositories.Except(AllowedRepositories.Keys)
            .Should().BeEmpty("there is no repository pattern here; inject IDocumentSession (CLAUDE.md section 1a)");
    }

    [Fact]
    public void Every_feature_endpoint_is_internal_and_lives_in_a_feature_and_action_namespace()
    {
        var endpoints = FeatureEndpoints();
        endpoints.Should().HaveCountGreaterThan(50);

        endpoints.Where(t => t.IsVisible).Select(t => t.FullName)
            .Should().BeEmpty("Features/* types stay internal (CLAUDE.md section 6)");

        // barakoCMS.Features.<Feature>.<Action>: at least two segments after Features.
        endpoints
            .Where(t => t.Namespace!.Substring(FeaturesPrefix.Length).Split('.').Length < 2)
            .Where(t => !FlatFeatureNamespaces.ContainsKey(t.Namespace!))
            .Select(t => t.FullName)
            .Should().BeEmpty("a slice is Features/<Feature>/<Action>/ (CLAUDE.md section 1a)");
    }

    [Fact]
    public void Every_validator_is_a_FastEndpoints_Validator()
    {
        var validators = CoreTypes().Where(IsFluentValidator).ToArray();
        validators.Should().HaveCountGreaterThan(20);

        validators
            .Where(t => !InheritsFrom(t, "FastEndpoints.Validator`1"))
            .Select(t => t.FullName)
            .Should().BeEmpty("validation goes through the FastEndpoints Validator<T> base, not a bare AbstractValidator<T> (CLAUDE.md section 1a)");
    }

    [Fact]
    public void Every_list_endpoint_takes_a_PaginatedRequest_or_is_allowlisted_with_a_reason()
    {
        var lists = FeatureEndpoints().Where(IsListEndpoint).ToArray();
        lists.Should().HaveCountGreaterThanOrEqualTo(10);
        lists.Where(IsBounded).Should().NotBeEmpty("most list endpoints are paginated, so finding none means the check is broken");

        lists
            .Where(t => !IsBounded(t) && !UnboundedListEndpoints.ContainsKey(t.FullName!))
            .Select(t => t.FullName)
            .Should().BeEmpty("a list endpoint is bounded: take PaginatedRequest, or add it here with the reason it cannot grow (CLAUDE.md section 1a)");
    }

    [Fact]
    public void Every_allowlist_entry_names_a_type_that_still_needs_it()
    {
        var byName = CoreTypes().Where(t => t.FullName is not null).ToDictionary(t => t.FullName!);

        UnboundedListEndpoints.Should().NotBeEmpty();
        foreach (var (name, reason) in UnboundedListEndpoints)
        {
            reason.Should().NotBeNullOrWhiteSpace();
            byName.Should().ContainKey(name, "an allowlist entry for a type that is gone excuses nothing and should be removed");
            IsBounded(byName[name]).Should().BeFalse($"{name} is paginated now, so it no longer needs the allowlist");
        }

        FlatFeatureNamespaces.Should().NotBeEmpty();
        foreach (var (ns, reason) in FlatFeatureNamespaces)
        {
            reason.Should().NotBeNullOrWhiteSpace();
            FeatureEndpoints().Should().Contain(t => t.Namespace == ns,
                $"{ns} has no endpoints directly in it any more, so it should leave the allowlist");
        }

        AllowedRepositories.Should().NotBeEmpty();
        foreach (var (name, reason) in AllowedRepositories)
        {
            reason.Should().NotBeNullOrWhiteSpace();
            byName.Should().ContainKey(name, "an allowlist entry for a type that is gone excuses nothing and should be removed");
        }
    }

    // What the formula language must never reach for. The folder is a closed grammar and a fixed
    // function table, so a way to run text as code or look up a member by name has no place in it.
    private static readonly string[] ForbiddenInExpressions =
    [
        "System.Reflection", "System.Linq.Expressions", "System.Linq.Dynamic", "System.Text.RegularExpressions",
        "Microsoft.CodeAnalysis", "System.CodeDom", "System.Runtime.Loader", "System.Management.Automation",
        "Regex", "dynamic", "Activator", "Assembly.", "MethodInfo", "GetType()", "Type.GetType", "Expression.Lambda",
        "CSharpScript", "ScriptOptions", "DynamicInvoke", "Process.Start", "Environment.GetEnvironmentVariable",
        "GetMethod", "GetProperty", "GetField", "PropertyInfo", "FieldInfo", "BindingFlags", "typeof(", "Enum.Parse",
        "Jint", "NCalc", "DynamicExpresso", "global::",
    ];

    // The only namespaces a file in the folder may bring in, and the folder's own. Nothing else of
    // barakoCMS is reachable, so the evaluator cannot grow a dependency on a service or a model.
    private static readonly string[] AllowedUsings =
    [
        "System", "System.Globalization", "System.Text", "System.Collections.Generic", "System.Linq",
    ];

    private const string ExpressionsNamespace = "barakoCMS.Core.Expressions";

    [Fact]
    public void The_expression_evaluator_uses_no_reflection_pattern_engine_or_scripting()
    {
        var folder = Path.Combine(RepoPaths.Root, "barakoCMS", "Core", "Expressions");
        var files = Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories);
        files.Should().NotBeEmpty("a scan that finds no files passes every check below");
        files.Should().Contain(f => f.EndsWith("Parser.cs", StringComparison.Ordinal));

        var found = new List<string>();
        foreach (var file in files)
        {
            var code = StripComments(File.ReadAllText(file));
            foreach (var word in ForbiddenInExpressions)
            {
                if (ContainsWord(code, word))
                {
                    found.Add($"{Path.GetFileName(file)}: {word}");
                }
            }

            found.AddRange(OutsideNamespaces(code).Select(n => $"{Path.GetFileName(file)}: {n}"));
        }

        found.Should().BeEmpty("the formula language is hand-written recursive descent over a fixed function table (#1420)");
    }

    [Fact]
    public void The_project_files_add_no_global_using_of_reflection_or_expression_trees()
    {
        var projectFiles = new[] { Path.Combine(RepoPaths.Root, "barakoCMS", "barakoCMS.csproj"), Path.Combine(RepoPaths.Root, "Directory.Build.props") };
        foreach (var file in projectFiles)
        {
            File.Exists(file).Should().BeTrue(file);
            var usings = XDocument.Load(file).Descendants("Using").Select(e => e.Attribute("Include")?.Value ?? string.Empty).ToArray();
            usings.Should().NotContain(u => u.StartsWith("System.Reflection", StringComparison.Ordinal)
                    || u.StartsWith("System.Linq.Expressions", StringComparison.Ordinal),
                $"{Path.GetFileName(file)} would give the evaluator folder reflection without a using line to scan");
        }
    }

    [Fact]
    public void The_expression_evaluator_scan_catches_what_it_forbids()
    {
        ContainsWord("using System.Text.RegularExpressions;", "System.Text.RegularExpressions").Should().BeTrue();
        ContainsWord("var r = new Regex(\"a\");", "Regex").Should().BeTrue();
        ContainsWord("dynamic x = 1;", "dynamic").Should().BeTrue();
        ContainsWord("// Regex is not used", "Regex").Should().BeTrue("the scan itself does not strip, StripComments does");
        StripComments("// Regex\nvar a = 1; /* dynamic */").Should().NotContain("Regex").And.NotContain("dynamic");
        ContainsWord("var notDynamicAtAll = 1;", "dynamic").Should().BeFalse("only whole words count");
        ContainsWord("var t = typeof(string);", "typeof(").Should().BeTrue();
        ContainsWord("t.GetMethod(\"x\")", "GetMethod").Should().BeTrue();
    }

    [Theory]
    [InlineData("var u = \"http://x\"; new Regex(\"a\");", "a // inside a string is not a comment")]
    [InlineData("var a = \"/*\"; new Regex(\"a\"); var b = \"*/\";", "a /* inside a string does not open a comment")]
    [InlineData("// holds /* here\nnew Regex(\"a\");", "a /* inside a line comment does not open a block")]
    [InlineData("var a = @\"x // \"\" /* y\"; new Regex(\"a\"); var b = @\"*/\";", "a verbatim string with a doubled quote")]
    [InlineData("var a = $\"{x}//\"; new Regex(\"a\");", "an interpolated string")]
    [InlineData("var a = $\"{(c ? \"/*\" : \"b\")}\"; new Regex(\"a\"); var b = \"*/\";", "a string inside an interpolation hole")]
    [InlineData("var a = \"\"\"\n// \"x\" /*\n\"\"\"; new Regex(\"a\"); var b = \"*/\";", "a raw string")]
    [InlineData("var a = $$\"\"\"{{x}} // {y} /*\"\"\"; new Regex(\"a\"); var b = \"*/\";", "an interpolated raw string")]
    [InlineData("var q = '\"'; new Regex(\"a\"); var b = \"//\";", "a quote in a char literal does not open a string")]
    [InlineData("var q = '\\''; new Regex(\"a\"); var b = \"*/\";", "an escaped quote in a char literal")]
    [InlineData("var a = \"x\\\"//\"; new Regex(\"a\");", "an escaped quote in a regular string")]
    public void The_comment_stripper_keeps_code_that_follows_a_string_or_a_comment_marker_inside_one(string source, string why)
    {
        StripComments(source).Should().Contain("new Regex(", why);
    }

    [Theory]
    [InlineData("var a = 1; // new Regex(\"a\")", "a line comment")]
    [InlineData("var a = 1; /* new Regex(\"a\") */ var b = 2;", "a block comment")]
    [InlineData("var a = \"x\"; // \"quoted\" new Regex(\"a\")", "a quote inside a comment")]
    [InlineData("var a = '\"'; // new Regex(\"a\")", "after a char literal holding a quote")]
    [InlineData("var a = @\"x\"; /* new Regex(\"a\") */", "after a verbatim string")]
    public void The_comment_stripper_removes_comments(string source, string why)
    {
        StripComments(source).Should().NotContain("Regex", why);
    }

    [Fact]
    public void The_expression_evaluator_using_scan_refuses_what_is_not_on_the_list()
    {
        OutsideNamespaces("using System;\nusing System.Globalization;\nusing barakoCMS.Core.Expressions;\nusing barakoCMS.Core.Expressions.Inner;")
            .Should().BeEmpty();
        OutsideNamespaces("namespace barakoCMS.Core.Expressions;\nvar a = 1;").Should().BeEmpty();
        OutsideNamespaces("using barakoCMS.Core.Validation;").Should().Contain("barakoCMS.Core.Validation");
        OutsideNamespaces("using static System.Math;").Should().Contain("System.Math");
        OutsideNamespaces("using M = System.Reflection;").Should().Contain("System.Reflection");
        OutsideNamespaces("global using System.IO;").Should().Contain("System.IO");
        OutsideNamespaces("var x = barakoCMS.Core.Validation.ComputedFields.MaxRound;").Should().Contain("barakoCMS.Core.Validation.ComputedFields.MaxRound");
        OutsideNamespaces("var x = barakoCMS.Core.ExpressionsExtra.Thing;").Should().NotBeEmpty("a namespace that only starts with the folder's name is another namespace");
        OutsideNamespaces(StripComments("// using barakoCMS.Core.Validation;")).Should().BeEmpty("the scan reads stripped code, as the caller gives it");
        ContainsWord("var x = global::System.Math.PI;", "global::").Should().BeTrue();
    }

    /// <summary>The namespaces a file reaches that are not on the list: a using outside it, or a barakoCMS name outside the folder.</summary>
    private static List<string> OutsideNamespaces(string code)
    {
        var outside = new List<string>();
        var directive = new System.Text.RegularExpressions.Regex(
            @"^\s*(?:global\s+)?using\s+(?:static\s+)?(?:[A-Za-z_]\w*\s*=\s*)?(?<ns>[A-Za-z_][\w.]*)\s*;",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        foreach (System.Text.RegularExpressions.Match match in directive.Matches(code))
        {
            var ns = match.Groups["ns"].Value;
            if (!AllowedUsings.Contains(ns) && !IsInExpressions(ns))
            {
                outside.Add(ns);
            }
        }

        var ours = new System.Text.RegularExpressions.Regex(@"\bbarakoCMS(?:\.[A-Za-z_]\w*)+");
        foreach (System.Text.RegularExpressions.Match match in ours.Matches(code))
        {
            if (!IsInExpressions(match.Value) && !outside.Contains(match.Value))
            {
                outside.Add(match.Value);
            }
        }

        return outside;
    }

    private static bool IsInExpressions(string name) =>
        name == ExpressionsNamespace || name.StartsWith(ExpressionsNamespace + ".", StringComparison.Ordinal);

    /// <summary>
    /// Removes comments and keeps everything else, string contents included, so a forbidden word in a
    /// string is still seen. It follows regular, verbatim, interpolated and raw strings, and char
    /// literals, so a comment marker inside a string is not a comment and a quote inside a comment
    /// or char literal does not open a string.
    /// </summary>
    private static string StripComments(string source)
    {
        var output = new System.Text.StringBuilder(source.Length);
        var at = 0;
        CopyCode(source, ref at, output, inHole: false);
        return output.ToString();
    }

    private static char CharAt(string s, int i) => i >= 0 && i < s.Length ? s[i] : '\0';

    private static void CopyCode(string s, ref int i, System.Text.StringBuilder o, bool inHole)
    {
        var depth = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '/' && CharAt(s, i + 1) == '/')
            {
                while (i < s.Length && s[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && CharAt(s, i + 1) == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? s.Length : end + 2;
                o.Append(' ');
                continue;
            }

            if (c == '\'')
            {
                CopyCharLiteral(s, ref i, o);
                continue;
            }

            if ((c is '"' or '$' or '@') && StringStart(s, i, out var dollars, out var verbatim, out var quotes))
            {
                CopyString(s, ref i, o, dollars, verbatim, quotes);
                continue;
            }

            if (inHole)
            {
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    if (depth == 0)
                    {
                        return;
                    }

                    depth--;
                }
            }

            o.Append(c);
            i++;
        }
    }

    private static void CopyCharLiteral(string s, ref int i, System.Text.StringBuilder o)
    {
        o.Append(s[i++]);
        if (CharAt(s, i) == '\\' && i + 1 < s.Length)
        {
            o.Append(s[i++]);
            o.Append(s[i++]);
        }

        while (i < s.Length && s[i] != '\'' && s[i] != '\n')
        {
            o.Append(s[i++]);
        }

        if (CharAt(s, i) == '\'')
        {
            o.Append(s[i++]);
        }
    }

    private static bool StringStart(string s, int i, out int dollars, out bool verbatim, out int quotes)
    {
        dollars = 0;
        verbatim = false;
        quotes = 0;
        var j = i;
        while (CharAt(s, j) is '$' or '@')
        {
            if (s[j] == '$')
            {
                dollars++;
            }
            else
            {
                verbatim = true;
            }

            j++;
        }

        while (CharAt(s, j + quotes) == '"')
        {
            quotes++;
        }

        return quotes > 0;
    }

    private static void CopyString(string s, ref int i, System.Text.StringBuilder o, int dollars, bool verbatim, int quotes)
    {
        while (s[i] != '"')
        {
            o.Append(s[i++]);
        }

        var raw = !verbatim && quotes >= 3;
        if (!raw && quotes >= 2 && !verbatim)
        {
            o.Append("\"\"");
            i += 2;
            return;
        }

        var open = raw ? quotes : 1;
        o.Append('"', open);
        i += open;
        while (i < s.Length)
        {
            var c = s[i];
            if (raw && c == '"')
            {
                var run = 0;
                while (CharAt(s, i + run) == '"')
                {
                    run++;
                }

                o.Append('"', run);
                i += run;
                if (run >= quotes)
                {
                    return;
                }

                continue;
            }

            if (!raw && c == '"')
            {
                if (verbatim && CharAt(s, i + 1) == '"')
                {
                    o.Append("\"\"");
                    i += 2;
                    continue;
                }

                o.Append(c);
                i++;
                return;
            }

            if (!raw && !verbatim && c == '\\')
            {
                o.Append(c);
                i++;
                if (i < s.Length)
                {
                    o.Append(s[i++]);
                }

                continue;
            }

            if (!raw && !verbatim && c == '\n')
            {
                return;
            }

            if (dollars > 0 && c == '{')
            {
                var run = 0;
                while (CharAt(s, i + run) == '{')
                {
                    run++;
                }

                var need = raw ? dollars : 1;
                if (run < need || (!raw && run % 2 == 0))
                {
                    o.Append('{', run);
                    i += run;
                    continue;
                }

                var literal = raw ? run - need : run - 1;
                o.Append('{', literal + need);
                i += literal + need;
                CopyCode(s, ref i, o, inHole: true);
                if (i < s.Length)
                {
                    o.Append('}', 1);
                    i++;
                    for (var k = 1; k < need && CharAt(s, i) == '}'; k++)
                    {
                        o.Append('}');
                        i++;
                    }
                }

                continue;
            }

            if (dollars > 0 && !raw && c == '}' && CharAt(s, i + 1) == '}')
            {
                o.Append("}}");
                i += 2;
                continue;
            }

            o.Append(c);
            i++;
        }
    }

    private static bool ContainsWord(string code, string word)
    {
        for (var at = code.IndexOf(word, StringComparison.Ordinal); at >= 0; at = code.IndexOf(word, at + 1, StringComparison.Ordinal))
        {
            var before = at == 0 ? ' ' : code[at - 1];
            var end = at + word.Length;
            var after = end >= code.Length ? ' ' : code[end];
            var startsWord = !char.IsLetterOrDigit(word[0]) || !(char.IsLetterOrDigit(before) || before == '_');
            var endsWord = !char.IsLetterOrDigit(word[^1]) || !(char.IsLetterOrDigit(after) || after == '_');
            if (startsWord && endsWord)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFluentValidator(Type type) =>
        type is { IsClass: true, IsAbstract: false } && InheritsFrom(type, "FluentValidation.AbstractValidator`1");

    private static bool InheritsFrom(Type type, string baseFullName)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
        {
            var name = t.IsGenericType ? t.GetGenericTypeDefinition().FullName : t.FullName;
            if (name == baseFullName)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCompilerGenerated(Type type) =>
        type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false)
        || type.Name.Contains('<');
}
