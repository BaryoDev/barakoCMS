using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using barakoCMS.Infrastructure.Health;
using Xunit;

namespace BarakoCMS.Tests;

[Collection("Sequential")]
public class HealthChecksUiRemovalTests
{
    private readonly IntegrationTestFixture _fixture;

    public HealthChecksUiRemovalTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static IConfiguration Config(string? enabled) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(enabled is null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?> { ["HealthChecksUI:Enabled"] = enabled })
            .Build();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the test needs the repository root to scan");
        return dir!.FullName;
    }

    [Fact]
    public void A_startup_warning_names_the_removed_setting_and_the_package_that_restores_it()
    {
        var messages = new List<string>();

        var warned = HealthChecksUiRemoval.WarnIfEnabled(Config("true"), messages.Add);

        warned.Should().BeTrue();
        messages.Should().HaveCount(1);
        messages[0].Should().Contain("HealthChecksUI:Enabled");
        messages[0].Should().Contain("AspNetCore.HealthChecks.UI");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public void No_warning_is_logged_when_the_old_setting_is_off_or_absent(string? value)
    {
        var messages = new List<string>();

        HealthChecksUiRemoval.WarnIfEnabled(Config(value), messages.Add).Should().BeFalse();

        messages.Should().BeEmpty();
    }

    [Fact]
    public void The_core_project_no_longer_references_the_health_checks_ui_packages()
    {
        var root = RepoRoot();

        var csproj = File.ReadAllText(Path.Combine(root, "barakoCMS", "barakoCMS.csproj"));
        var props = File.ReadAllText(Path.Combine(root, "Directory.Packages.props"));

        csproj.Should().NotContain("AspNetCore.HealthChecks.UI");
        props.Should().NotContain("AspNetCore.HealthChecks.UI");
    }

    [Fact]
    public void No_module_project_references_or_calls_the_removed_health_checks_ui_types()
    {
        var root = RepoRoot();
        var modules = Directory.GetDirectories(root, "BarakoCMS.*")
            .Where(d => !d.EndsWith("BarakoCMS.Tests", StringComparison.Ordinal))
            .ToList();
        modules.Should().NotBeEmpty();

        var files = modules
            .SelectMany(m => Directory.EnumerateFiles(m, "*.*", SearchOption.AllDirectories))
            .Where(f => (f.EndsWith(".cs") || f.EndsWith(".csproj"))
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
        files.Should().NotBeEmpty();

        var offenders = files
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                return text.Contains("HealthChecks.UI") || text.Contains("AddHealthChecksUI")
                       || text.Contains("UseHealthChecksUI") || text.Contains("UIResponseWriter");
            })
            .ToList();

        offenders.Should().BeEmpty();
    }

    [Fact]
    public async Task The_live_health_endpoint_answers_200_with_a_json_status_body()
    {
        using var client = _fixture.CreateClient();

        var response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        using var json = JsonDocument.Parse(body);
        json.RootElement.EnumerateObject().Should().HaveCount(1);
        json.RootElement.GetProperty("status").GetString().Should().Be("Healthy");
    }
}
