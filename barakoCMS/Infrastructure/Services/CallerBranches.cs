namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// The branches a member may work in, kept in the membership profile attribute <c>branches</c> as
/// comma separated ids, and the request's own branch, which is never stored.
/// </summary>
internal static class CallerBranches
{
    /// <summary>Stored: the branches the user may work in.</summary>
    public const string BranchesAttribute = "branches";

    /// <summary>Never stored: the request's branch, set by the resolver for access rules.</summary>
    public const string BranchAttribute = "branch";

    public const int MaxBranches = 32;

    /// <summary>The ids in a stored value, or none when it is empty, too long or any part is not an id.</summary>
    public static IReadOnlyList<Guid> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length > MaxBranches)
            return [];

        var ids = new List<Guid>(parts.Length);
        foreach (var part in parts)
        {
            if (!Guid.TryParse(part, out var id))
                return [];
            ids.Add(id);
        }

        return ids;
    }

    /// <summary>The branches in a member profile.</summary>
    public static IReadOnlyList<Guid> Of(IReadOnlyDictionary<string, string>? profile) =>
        profile is not null && profile.TryGetValue(BranchesAttribute, out var value) ? Parse(value) : [];

    /// <summary>
    /// A copy of the profile with <c>branch</c> set to the request's branch when the member holds it,
    /// and with no <c>branch</c> otherwise. The stored profile is not changed.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? WithRequestBranch(
        IReadOnlyDictionary<string, string>? profile, Guid? requestBranch)
    {
        var copy = profile is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(profile);
        copy.Remove(BranchAttribute);

        if (requestBranch is { } branch && Of(profile).Contains(branch))
            copy[BranchAttribute] = branch.ToString();

        return copy;
    }
}
