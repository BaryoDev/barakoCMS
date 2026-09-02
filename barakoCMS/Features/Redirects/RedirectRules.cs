using barakoCMS.Models;

namespace barakoCMS.Features.Redirects;

/// <summary>Why a redirect may not be saved, or null.</summary>
internal static class RedirectRules
{
    /// <summary>
    /// How far a chain is followed before it is called too long.
    /// </summary>
    /// <remarks>
    /// A chain longer than this is refused even if it terminates. Browsers stop following redirects
    /// somewhere around twenty, so a chain of ten is already a page nobody reaches quickly, and save
    /// time is the only moment anybody can fix it cheaply.
    /// </remarks>
    public const int MaxChain = 10;

    /// <summary>
    /// Checks one new or edited rule against the rules already stored.
    /// </summary>
    /// <param name="from">The normalised path being redirected.</param>
    /// <param name="to">The normalised destination.</param>
    /// <param name="existing">Every stored rule, as a map from path to destination.</param>
    /// <returns>The reason it is refused, or null.</returns>
    /// <remarks>
    /// Checked here rather than when somebody follows the chain. A loop discovered at request time is
    /// discovered on the 404 path, which is exactly when the site is already having a bad day, and by
    /// somebody who cannot fix it. At save time it is one message to the person who caused it.
    ///
    /// The map is passed in rather than queried here, so this is pure and testable without a
    /// database. It also puts the caller in charge of what "existing" means, which matters on an
    /// edit: the rule being replaced has to be out of the map or every edit looks like a loop with
    /// itself.
    /// </remarks>
    public static string? Refuse(string from, string to, IReadOnlyDictionary<string, string> existing)
    {
        if (from.Length == 0 || to.Length == 0)
        {
            return "A redirect needs a path to move from and a path to move to.";
        }

        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            // The one-step loop, which is what a typo produces and what a request-time check would
            // report as "too many redirects" long after anybody remembers writing it.
            return $"'{from}' redirects to itself.";
        }

        // Walk the chain the new rule would create. Every hop is a rule that already exists, so this
        // ends at a path nobody redirects, back at the rule being added, or at the cap.
        var seen = new HashSet<string>(StringComparer.Ordinal) { from };
        var path = to;

        for (var hop = 0; hop < MaxChain; hop++)
        {
            if (!seen.Add(path))
            {
                return $"'{from}' to '{to}' completes a redirect loop, which would leave a visitor "
                     + "bouncing between pages until their browser gives up.";
            }

            if (!existing.TryGetValue(path, out var next))
            {
                // The chain ends somewhere real.
                return null;
            }

            path = next;
        }

        return $"'{from}' to '{to}' makes a chain longer than {MaxChain} redirects. Point it at the "
             + "final destination instead, which is where a visitor ends up anyway and in one hop.";
    }
}
