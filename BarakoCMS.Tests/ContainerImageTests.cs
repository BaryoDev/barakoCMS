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

    /// <summary>The two ways a Dockerfile can name uid 0.</summary>
    private static readonly string[] RootSpellings = ["root", "0"];

    public static TheoryData<string> RuntimeImages() => new() { "Dockerfile", "Dockerfile.suite" };

    [Theory]
    [MemberData(nameof(RuntimeImages))]
    public void The_image_drops_to_a_non_root_user_before_its_entrypoint(string dockerfile)
    {
        var path = Path.Combine(RepoRoot(), dockerfile.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).Should().BeTrue("{0} is one of the published images", dockerfile);

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
          + "uid 1654; nothing here needs privilege, because 8080 is above 1024 and the app writes "
          + "nothing to the container filesystem", dockerfile);

        // USER takes name or numeric id, with an optional group, so root has six spellings: root, 0,
        // root:root, 0:0, root:0, 0:root. Comparing against the literal "root" catches one of them
        // and passes on an image running as uid 0 by number, which is the same image.
        //
        // The uid is what decides privilege here, so the group half is deliberately not checked. A
        // non-zero uid in group 0 is not root, and asserting on it would fail images that are fine.
        var uid = user!.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1].Split(':')[0];

        RootSpellings.Should().NotContain(uid,
            "{0} names root, by name or by number. USER root and USER 0 produce the same image as "
          + "omitting the line entirely", dockerfile);
    }

    // Every spelling of root the previous test has to reject. Without this, the check above is a
    // string comparison nobody has confirmed rejects anything.
    [Theory]
    [InlineData("USER root")]
    [InlineData("USER 0")]
    [InlineData("USER root:root")]
    [InlineData("USER 0:0")]
    [InlineData("USER root:0")]
    [InlineData("USER 0:root")]
    public void Every_spelling_of_root_is_recognised_as_root(string directive)
    {
        var uid = directive.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1].Split(':')[0];

        RootSpellings.Should().Contain(uid, "{0} runs as uid 0", directive);
    }

    [Theory]
    [InlineData("USER app")]
    [InlineData("USER nextjs")]
    [InlineData("USER 1654")]
    [InlineData("USER 1654:1654")]
    [InlineData("USER app:root")]
    public void A_non_root_uid_is_accepted_whatever_its_group(string directive)
    {
        var uid = directive.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1].Split(':')[0];

        RootSpellings.Should().NotContain(uid,
            "{0} runs as a non-root uid, and the group half does not make it root", directive);
    }

    // The control. Without it the assertion above passes on a file that does not exist, or on one
    // whose final stage was never found, and this project has shipped that shape of gate before.
    [Fact]
    public void The_dockerfiles_being_checked_are_the_ones_that_are_published()
    {
        var root = RepoRoot();
        var release = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));

        // Whole trimmed lines, not substrings. "file: Dockerfile" is contained in
        // "file: Dockerfile.suite", so a containment check would still pass if the decaf build were
        // deleted, and the control would go on reporting that a file nobody publishes is guarded.
        var declared = release.Split('\n').Select(l => l.Trim()).ToArray();

        foreach (var f in new[] { "Dockerfile", "Dockerfile.suite" })
        {
            declared.Should().Contain($"file: {f}",
                "{0} is asserted above, so the release has to be the thing that builds it. If this "
              + "fails, either the release changed or the test is guarding a file nobody ships", f);
        }
    }
}
