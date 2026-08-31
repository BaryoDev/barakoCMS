using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// No claim lookup may spell a claim type as the name of the constant that holds it.
/// </summary>
/// <remarks>
/// Three endpoints looked up the literal string
/// <c>"System.Security.Claims.ClaimTypes.NameIdentifier"</c>. That is the name of a constant, not
/// its value (which is a URI), so it matched nothing on any token this project issues and the
/// <c>"UserId"</c> fallback beside it was always what ran. Harmless and invisible, which is the
/// problem: it reads as though a second identity source is being consulted, and someone maintaining
/// authentication would reasonably believe it.
///
/// This is a source check because there is no behaviour to assert. Removing a check that never
/// matched changes no response, so the only thing a test can pin is that the literal is gone.
/// </remarks>
public class ClaimNameTests
{
    private const string ConstantName = "System.Security.Claims.ClaimTypes.";

    [Fact]
    public void No_source_file_looks_up_a_claim_by_the_name_of_its_constant()
    {
        var offenders = new List<string>();

        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            if (text.Contains($"\"{ConstantName}", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        offenders.Should().BeEmpty(
            "a quoted \"System.Security.Claims.ClaimTypes.X\" is the constant's name, not its value, "
            + "so it can never match a claim; use ClaimTypes.X or the claim name the token carries");
    }

    private static IEnumerable<string> SourceFiles()
    {
        var root = RepositoryRoot();
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(directory);
            if (!name.Equals("barakoCMS", StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith("BarakoCMS.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (name.Equals("BarakoCMS.Tests", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "barakoCMS.sln")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the test has to find the solution root to scan the source");
        return directory!.FullName;
    }
}
