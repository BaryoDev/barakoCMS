using FluentAssertions;

namespace BarakoCMS.Tests;

/// <summary>
/// Where the repository sits on disk, for tests that read shipped files (migrations, manifests,
/// compose files, sources).
/// </summary>
internal static class RepoPaths
{
    private static readonly Lazy<string> RootPath = new(Find);

    /// <summary>The repository root, found by walking up from the test binaries to Directory.Build.props.</summary>
    public static string Root => RootPath.Value;

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the tests must be able to find the repository root");
        return dir!.FullName;
    }
}
