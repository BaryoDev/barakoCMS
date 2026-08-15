using Xunit;
using FluentAssertions;
using System.Xml.Linq;

namespace BarakoCMS.Tests;

/// <summary>
/// Guardrails for what every published package must carry.
///
/// These exist because packaging mistakes are invisible until someone else hits them: a module with
/// no README renders as a blank page on nuget.org, a missing discovery tag means it never shows up
/// alongside its siblings, and a module whose source moved without its version moving is silently
/// skipped by <c>--skip-duplicate</c> and simply never ships. That last one has swallowed a fix
/// twice, once a security fix.
///
/// A convention nothing enforces is a convention that decays as modules are added, so it is checked
/// here rather than written down and hoped for.
/// </summary>
public class PackagingTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the tests must be able to find the repository root");
        return dir!.FullName;
    }

    /// <summary>Every project that produces a NuGet package, with its parsed csproj.</summary>
    public static TheoryData<string> PackableProjects()
    {
        var data = new TheoryData<string>();
        foreach (var (path, _) in Packable()) data.Add(Path.GetFileName(Path.GetDirectoryName(path))!);
        return data;
    }

    private static List<(string Path, XDocument Doc)> Packable()
    {
        var found = new List<(string, XDocument)>();
        foreach (var proj in Directory.EnumerateFiles(RepoRoot(), "*.csproj", SearchOption.AllDirectories))
        {
            if (proj.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var doc = XDocument.Load(proj);
            var packable = doc.Descendants("IsPackable").FirstOrDefault()?.Value;
            if (string.Equals(packable, "true", StringComparison.OrdinalIgnoreCase))
                found.Add((proj, doc));
        }
        return found;
    }

    private static string ProjectDir(string name) =>
        Packable().Single(p => Path.GetFileName(Path.GetDirectoryName(p.Path)) == name).Path is var path
            ? Path.GetDirectoryName(path)!
            : throw new InvalidOperationException(name);

    [Fact]
    public void There_are_packable_projects_to_check()
    {
        // Guards the guard: if the discovery ever breaks, every other test here would vacuously pass.
        Packable().Should().HaveCountGreaterThan(5);
    }

    [Theory]
    [MemberData(nameof(PackableProjects))]
    public void Every_package_ships_a_readme(string project)
    {
        // NuGet renders this as the package page. Without it the page is blank, which is where
        // someone decides whether to trust the package at all.
        File.Exists(Path.Combine(ProjectDir(project), "README.md"))
            .Should().BeTrue($"{project} is published to NuGet, so it needs a README.md next to its .csproj");
    }

    [Theory]
    [MemberData(nameof(PackableProjects))]
    public void Every_package_declares_an_id_a_version_and_a_description(string project)
    {
        var doc = Packable().Single(p => Path.GetFileName(Path.GetDirectoryName(p.Path)) == project).Doc;

        doc.Descendants("PackageId").FirstOrDefault()?.Value
            .Should().NotBeNullOrWhiteSpace($"{project} needs an explicit PackageId");
        doc.Descendants("Version").FirstOrDefault()?.Value
            .Should().NotBeNullOrWhiteSpace($"{project} needs a Version — the release gate reads it");
        doc.Descendants("Description").FirstOrDefault()?.Value
            .Should().NotBeNullOrWhiteSpace($"{project} needs a Description — it is the one line shown in search results");
    }

    [Theory]
    [MemberData(nameof(PackableProjects))]
    public void No_module_pins_metadata_that_belongs_in_the_shared_props(string project)
    {
        var doc = Packable().Single(p => Path.GetFileName(Path.GetDirectoryName(p.Path)) == project).Doc;

        // Shared metadata drifts the moment it is duplicated: one module keeps an old licence or
        // loses the icon, and nothing notices until it is published.
        foreach (var shared in new[]
                 {
                     "PackageIcon", "PackageTags", "PackageLicenseExpression",
                     "Copyright", "RepositoryUrl", "TargetFramework",
                 })
        {
            doc.Descendants(shared).Should().BeEmpty(
                $"{project} should inherit {shared} from Directory.Build.props rather than setting its own");
        }
    }

    [Fact]
    public void The_shared_props_carry_the_packaging_metadata()
    {
        var props = XDocument.Load(Path.Combine(RepoRoot(), "Directory.Build.props"));

        foreach (var required in new[]
                 {
                     "PackageIcon", "PackageTags", "PackageLicenseExpression", "Copyright",
                     "RepositoryUrl", "PackageProjectUrl", "GenerateDocumentationFile",
                     "IncludeSymbols", "PublishRepositoryUrl",
                 })
        {
            props.Descendants(required).Should().NotBeEmpty(
                $"{required} is what makes every package land on nuget.org complete");
        }

        // The discovery tag is the whole point of tagging: one search returns the module set.
        props.Descendants("PackageTags").Single().Value
            .Should().Contain("barakocms-module");
    }

    [Fact]
    public void The_icon_referenced_by_the_shared_props_exists_and_is_a_real_png()
    {
        var icon = Path.Combine(RepoRoot(), "assets", "icon.png");
        File.Exists(icon).Should().BeTrue("every package references assets/icon.png");

        // NuGet rejects an icon over 1MB and does not accept SVG, so check it is genuinely a PNG
        // rather than a renamed file — the pack succeeds either way and the push is what fails.
        var header = new byte[8];
        using (var fs = File.OpenRead(icon)) fs.ReadExactly(header);
        header.Should().Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
            "the icon must be a real PNG");
        new FileInfo(icon).Length.Should().BeLessThan(1024 * 1024, "NuGet rejects icons over 1MB");
    }
}
