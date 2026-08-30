using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// Guardrails for the published container images.
/// </summary>
/// <remarks>
/// Both API images ran as root until 4.0 while the admin image did not, which is what an omission
/// looks like rather than a decision. Root turns a code-execution bug from a contained failure into
/// a container-escape attempt, and AWS Marketplace refuses container products that run privileged
/// by default, so it is a security property and a distribution requirement at once.
///
/// Checked against the Dockerfile rather than a built image on purpose. The risk being guarded is
/// somebody deleting a line, and this catches that in the unit suite without needing Docker. That
/// the image still boots as a non-root user is proved elsewhere and by something better: both
/// upgrade-check.sh and restore-check.sh start the real image in CI, so a permission failure at
/// startup fails those jobs.
/// </remarks>
public class ContainerImageTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the tests must be able to find the repository root");
        return dir!.FullName;
    }

    public static TheoryData<string> RuntimeImages() => new() { "Dockerfile", "Dockerfile.suite", "admin/Dockerfile" };

    [Theory]
    [MemberData(nameof(RuntimeImages))]
    public void The_image_drops_to_a_non_root_user_before_its_entrypoint(string dockerfile)
    {
        var path = Path.Combine(RepoRoot(), dockerfile.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).Should().BeTrue("{0} is one of the three published images", dockerfile);

        var lines = File.ReadAllLines(path);

        // Only the final stage matters. A USER in a build stage does not affect what the published
        // image runs as, so a naive search for the word would pass on an image that still runs as
        // root, which is the exact failure this test exists to catch.
        var finalStage = Array.FindLastIndex(lines, l => l.TrimStart().StartsWith("FROM ", StringComparison.OrdinalIgnoreCase));
        finalStage.Should().BeGreaterThanOrEqualTo(0, "{0} has to declare a base image", dockerfile);

        var user = lines.Skip(finalStage)
            .Select(l => l.Trim())
            .LastOrDefault(l => l.StartsWith("USER ", StringComparison.OrdinalIgnoreCase));

        user.Should().NotBeNull(
            "{0} runs as root without a USER in its final stage. The .NET base images ship app as "
          + "uid 1654 and the admin image uses nextjs; nothing here needs privilege, because 8080 is "
          + "above 1024 and the app writes nothing to the container filesystem", dockerfile);

        user!.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]
            .Should().NotBe("root", "{0} names root explicitly, which is worse than omitting it", dockerfile);
    }

    // The control. Without it the assertion above passes on a file that does not exist, or on one
    // whose final stage was never found, and this project has shipped that shape of gate before.
    [Fact]
    public void The_dockerfiles_being_checked_are_the_ones_that_are_published()
    {
        var root = RepoRoot();
        var release = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));

        foreach (var f in new[] { "Dockerfile", "Dockerfile.suite", "admin/Dockerfile" })
        {
            release.Should().Contain($"file: {f}",
                "{0} is asserted above, so the release has to be the thing that builds it. If this "
              + "fails, either the release changed or the test is guarding a file nobody ships", f);
        }
    }
}
