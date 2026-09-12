using FluentAssertions;
using Xunit;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// No authorization decision keys on the literal role name "SuperAdmin", and no custom role can
/// take a seeded name.
/// </summary>
/// <remarks>
/// PermissionResolver bypassed every capability check for any role whose <c>Name</c> was
/// "SuperAdmin", while role create put no guard on the name a caller supplied. A caller holding
/// only <c>manage_roles</c> could therefore POST a role called "SuperAdmin", assign it, and bypass
/// every gate, including <c>erase_content</c>, which is deliberately withheld from Admin.
///
/// Two things were wrong, and both are pinned here because fixing either alone leaves a hole.
///
/// The resolver was re-deriving a system role from its name when the id is the key, which is the
/// rule SystemRoles already stated for deletion. That is fixed by keying on the seeded id.
///
/// The id is not reachable everywhere. TokenIssuer puts role *names* into the JWT as role claims,
/// and SensitivityService reads one back with <c>IsInRole("SuperAdmin")</c> to skip scrubbing. A
/// claim carries no id, so that path cannot be fixed the same way: the name itself has to be
/// unavailable. Hence the reserved-name rule on both write paths, which is the only point both
/// the resolver path and the claims path pass through.
/// </remarks>
public class SuperAdminNameBypassTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the test must be able to find the repository root");
        return dir!.FullName;
    }

    [Fact]
    public void PermissionResolver_does_not_bypass_on_a_role_name()
    {
        var path = Path.Combine(RepoRoot(), "barakoCMS", "Infrastructure", "Services", "PermissionResolver.cs");
        File.Exists(path).Should().BeTrue("the resolver is the file this regression lives in");

        var source = File.ReadAllText(path);

        source.Should().NotContain(
            "r.Name == \"SuperAdmin\"",
            "the resolver must identify the seeded SuperAdmin role by its id, not its name: a custom "
            + "role that takes the name would otherwise inherit a bypass of every capability gate");

        source.Should().Contain(
            "SystemRoles.SuperAdminRoleId",
            "the bypass is still expected, keyed on the seeded id");
    }

    [Theory]
    [InlineData("SuperAdmin")]
    [InlineData("Admin")]
    [InlineData("HR")]
    [InlineData("User")]
    public void Seeded_names_are_reserved(string name)
        => SystemRoles.IsReservedName(name).Should().BeTrue();

    [Theory]
    [InlineData("superadmin")]
    [InlineData("SUPERADMIN")]
    [InlineData("  SuperAdmin  ")]
    public void Reservation_is_case_and_whitespace_insensitive(string name)
        => SystemRoles.IsReservedName(name).Should().BeTrue(
            "a caller who cannot use the exact spelling must not get the claim by changing case or "
            + "padding it: the role claim is compared with IsInRole, which is case-insensitive");

    [Theory]
    [InlineData("Acme Editor")]
    [InlineData("Superadministrator")]
    [InlineData("Admins")]
    [InlineData("")]
    [InlineData(null)]
    public void Ordinary_names_are_not_reserved(string? name)
        => SystemRoles.IsReservedName(name).Should().BeFalse();

    [Fact]
    public void Both_role_write_paths_check_the_reservation()
    {
        var root = RepoRoot();
        foreach (var relative in new[]
                 {
                     Path.Combine("barakoCMS", "Features", "Roles", "Create", "Endpoint.cs"),
                     Path.Combine("barakoCMS", "Features", "Roles", "Update", "Endpoint.cs"),
                 })
        {
            var path = Path.Combine(root, relative);
            File.Exists(path).Should().BeTrue($"{relative} is a role write path");

            File.ReadAllText(path).Should().Contain(
                "SystemRoles.IsReservedName",
                $"{relative} writes a role name, so it must refuse a reserved one. Leaving either "
                + "path unguarded reopens the claims-based bypass, because the JWT carries the name");
        }
    }
}
