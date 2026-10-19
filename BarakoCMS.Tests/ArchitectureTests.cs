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

        var csproj = Path.Combine(ComposeDefaultsTests.RepoRoot(), "barakoCMS", "barakoCMS.csproj");
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

        var root = Path.Combine(ComposeDefaultsTests.RepoRoot(), "barakoCMS", "Features");
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
