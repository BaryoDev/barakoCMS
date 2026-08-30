using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Guardrails for the compose files a new user runs first.
/// </summary>
/// <remarks>
/// The root file was labelled local-development-only and still shipped three things that are unsafe
/// to copy: the developer's whole kubeconfig bind-mounted into the app container, a literal postgres
/// password that no variable could override, and postgres published on every interface of the host.
/// "Local only" is a comment, not a control, and people copy what works.
///
/// Read as text rather than through a YAML parser on purpose: the risk is a line coming back, and
/// this catches that in the unit suite without needing Docker. See issue #257.
/// </remarks>
public class ComposeDefaultsTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the tests must be able to find the repository root");
        return dir!.FullName;
    }

    public static TheoryData<string> ComposeFiles() => new()
    {
        "docker-compose.yml",
        "docker-compose.hub.yml",
        "docker-compose.prod.yml",
        "quickstart/docker-compose.yml"
    };

    private static string[] Lines(string composeFile)
    {
        var path = Path.Combine(RepoRoot(), composeFile.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).Should().BeTrue("{0} is one of the shipped compose files", composeFile);
        return File.ReadAllLines(path);
    }

    /// <summary>A volume entry whose source is a path on the host rather than a named volume.</summary>
    private static readonly Regex HostBindMount =
        new(@"^-\s*[""']?(\$\{?HOME\}?|~|/(etc|root|home|var/run))", RegexOptions.Compiled);

    [Theory]
    [MemberData(nameof(ComposeFiles))]
    public void No_compose_file_mounts_host_credentials_into_a_container(string composeFile)
    {
        var offenders = Lines(composeFile)
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith('#'))
            .Where(l => HostBindMount.IsMatch(l))
            .ToArray();

        offenders.Should().BeEmpty(
            "{0} must not bind a host path holding credentials into a container. A kubeconfig mount "
          + "hands every context and token in it to anything running in the container, and the "
          + "Kubernetes monitor only ever needs one namespace", composeFile);
    }

    [Theory]
    [MemberData(nameof(ComposeFiles))]
    public void Every_database_password_comes_from_a_variable(string composeFile)
    {
        var literals = Lines(composeFile)
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith('#'))
            .Where(l => l.Contains("POSTGRES_PASSWORD") || l.Contains("Password="))
            .Where(l => !l.Contains('$'))
            .ToArray();

        literals.Should().BeEmpty(
            "{0} bakes a password in, so setting the password variable leaves the app, the database "
          + "and the backup out of step and the built-in value keeps working", composeFile);
    }

    /// <summary>A published port mapping, capturing the optional host interface it binds to.</summary>
    private static readonly Regex PublishedPort =
        new(@"^-\s*""?(?<host>[0-9.]+:)?(?<hostPort>\d+):(?<containerPort>\d+)""?\s*$", RegexOptions.Compiled);

    [Theory]
    [MemberData(nameof(ComposeFiles))]
    public void Postgres_is_not_published_off_host(string composeFile)
    {
        var exposed = Lines(composeFile)
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith('#'))
            .Select(l => PublishedPort.Match(l))
            .Where(m => m.Success && m.Groups["containerPort"].Value == "5432")
            .Where(m => !m.Groups["host"].Value.StartsWith("127.0.0.1", StringComparison.Ordinal))
            .Select(m => m.Value)
            .ToArray();

        exposed.Should().BeEmpty(
            "{0} publishes postgres on every interface of the host. Combined with a default password "
          + "that is an open database; the app reaches it over the compose network, so a mapping is "
          + "only for a local client and belongs on 127.0.0.1", composeFile);
    }
}
