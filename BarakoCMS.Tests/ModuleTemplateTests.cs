using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// What the module template promises about the packages it generates against. The generated module
/// itself is built and tested by <c>scripts/check-module-template.sh</c>, which CI runs; a text
/// assertion here would only repeat what that script executes.
/// </summary>
public class ModuleTemplateTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the tests must be able to find the repository root");
        return dir!.FullName;
    }

    private static string TemplateDir() =>
        Path.Combine(RepoRoot(), "BarakoCMS.Templates", "templates", "barakocms-module");

    private static string ProjectVersion(string project) =>
        XDocument.Load(Path.Combine(RepoRoot(), project, $"{project}.csproj"))
            .Descendants("Version").Single().Value;

    private static string TemplateDefault(string symbol)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(TemplateDir(), ".template.config", "template.json")));
        return doc.RootElement.GetProperty("symbols").GetProperty(symbol).GetProperty("defaultValue").GetString()!;
    }

    [Fact]
    public void The_template_defaults_to_the_core_version_in_this_checkout()
    {
        // A generated module compiles against the BarakoCMS the template shipped beside. Bumping
        // core's <Version> is the release switch, and this is what makes the template follow it
        // rather than keep pointing a new module at the previous release.
        TemplateDefault("BarakoCMSVersion").Should().Be(ProjectVersion("barakoCMS"));
    }

    [Fact]
    public void The_template_defaults_to_the_harness_version_in_this_checkout()
    {
        TemplateDefault("TestingVersion").Should().Be(ProjectVersion("BarakoCMS.Testing"));
    }

    [Fact]
    public void The_generated_props_carry_the_discovery_tag_and_the_module_project_pins_nothing_shared()
    {
        var props = XDocument.Load(Path.Combine(TemplateDir(), "Directory.Build.props"));
        props.Descendants("PackageTags").Single().Value.Should().Contain("barakocms-module");

        // Same rule PackagingTests applies to the first-party modules: shared metadata lives in
        // the props file, so a generated module inherits it rather than drifting on its own copy.
        var module = XDocument.Load(Path.Combine(TemplateDir(), "src", "MyBarakoModule", "MyBarakoModule.csproj"));
        foreach (var shared in new[] { "PackageIcon", "PackageTags", "PackageLicenseExpression", "Authors", "TargetFramework" })
            module.Descendants(shared).Should().BeEmpty($"{shared} belongs in the generated Directory.Build.props");
    }
}
