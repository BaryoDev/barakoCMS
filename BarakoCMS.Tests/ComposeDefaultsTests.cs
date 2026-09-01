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

    /// <summary>
    /// No compose file ships a literal initial-admin password (#271).
    /// </summary>
    /// <remarks>
    /// Two of them defaulted it to "changeme-in-production", so `docker compose up` with no .env
    /// produced a SuperAdmin account whose password is in this repository and in every copy of it.
    /// The seeder generates one and prints it once when the variable is empty, which keeps a
    /// zero-configuration first run working without publishing the credential.
    ///
    /// The pattern deliberately allows `${VAR:-}` and `${VAR:?...}` and rejects `${VAR:-literal}`.
    /// A default that is a real password is the defect; an empty default and a required one are the
    /// two fixes.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ComposeFiles))]
    public void No_compose_file_defaults_the_admin_password_to_a_literal(string composeFile)
    {
        var offenders = Lines(composeFile)
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith('#'))
            .Where(l => l.Contains("InitialAdmin__Password") || l.Contains("InitialAdmin:Password"))
            .Where(l => !LiteralFreeAdminPassword.IsMatch(l))
            .ToArray();

        offenders.Should().BeEmpty(
            "{0} bakes an initial admin password in, so a stack brought up with no .env has a "
          + "SuperAdmin login that is published in this repository", composeFile);
    }

    /// <summary>The two acceptable shapes: an empty default, or a required variable.</summary>
    private static readonly Regex LiteralFreeAdminPassword =
        new(@"\$\{[A-Za-z_][A-Za-z0-9_]*(:-\}|:\?|\})", RegexOptions.Compiled);

    // The control for the test above. Without it the regex is a claim nobody has checked, and it
    // would go on reporting clean if it stopped matching anything at all.
    [Theory]
    [InlineData("- InitialAdmin__Password=${ADMIN_PASSWORD:-changeme-in-production}")]
    [InlineData("- InitialAdmin__Password=changeme-in-production")]
    [InlineData("InitialAdmin__Password: ${ADMIN_PASSWORD:-admin}")]
    public void A_literal_admin_password_default_is_recognised(string line)
    {
        LiteralFreeAdminPassword.IsMatch(line).Should().BeFalse("{0} ships a usable password", line);
    }

    [Theory]
    [InlineData("- InitialAdmin__Password=${ADMIN_PASSWORD:-}")]
    [InlineData("InitialAdmin__Password: ${ADMIN_PASSWORD:?set ADMIN_PASSWORD in .env}")]
    [InlineData("InitialAdmin__Password: ${ADMIN_PASSWORD}")]
    public void An_empty_or_required_admin_password_is_accepted(string line)
    {
        LiteralFreeAdminPassword.IsMatch(line).Should().BeTrue("{0} publishes no password", line);
    }

    /// <summary>
    /// The compose that runs the published images does not turn the OpenAPI explorer on by
    /// inheriting it from ASPNETCORE_ENVIRONMENT (#271).
    /// </summary>
    /// <remarks>
    /// Swagger follows the environment when Swagger:Enabled is unset, and docker-compose.hub.yml
    /// defaults the environment to Development, so the file people copy toward a host exposed the
    /// whole API surface without anyone choosing it. Saying it explicitly means changing the
    /// environment no longer changes what is published as a side effect.
    ///
    /// docker-compose.yml is not in this list on purpose. It builds from source, says LOCAL
    /// DEVELOPMENT ONLY at the top, and the explorer is the point there; it still sets the flag
    /// explicitly, but to true.
    /// </remarks>
    [Fact]
    public void The_published_image_compose_does_not_leave_swagger_to_the_environment()
    {
        var setting = Lines("docker-compose.hub.yml")
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith('#'))
            .SingleOrDefault(l => l.Contains("Swagger__Enabled"));

        setting.Should().NotBeNull(
            "docker-compose.hub.yml defaults ASPNETCORE_ENVIRONMENT to Development, and Swagger "
          + "follows the environment when the flag is unset");

        setting.Should().NotContain(":-true",
            "the flag is there to keep the explorer off unless somebody asks for it");
    }

    /// <summary>
    /// No Fly.io app configuration is committed (#271).
    /// </summary>
    /// <remarks>
    /// `app` in fly.toml is a name unique across all of Fly, so a committed one cannot be right for
    /// two people at once: the next person to run `fly deploy` from a clone either collides on the
    /// name or deploys into the maintainer's app. Same reasoning as launchSettings.json, which
    /// .gitignore has excluded for the same class of reason since 3.x.
    /// </remarks>
    [Fact]
    public void No_flyio_app_configuration_is_committed()
    {
        var root = RepoRoot();

        File.Exists(Path.Combine(root, "fly.toml")).Should().BeFalse(
            "fly.toml names a globally unique Fly app, so committing one points every adopter's "
          + "deploy at it. `fly launch` generates the file; .gitignore keeps it local");

        File.ReadAllLines(Path.Combine(root, ".gitignore"))
            .Select(l => l.Trim())
            .Should().Contain("fly.toml",
                "without the ignore the file comes back the first time anyone runs `fly launch`");
    }

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
