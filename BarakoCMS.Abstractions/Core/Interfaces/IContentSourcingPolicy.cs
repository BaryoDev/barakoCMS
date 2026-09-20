using barakoCMS.Models;

namespace barakoCMS.Core.Interfaces;

/// <summary>
/// Reads and records the one-way decision about whether a content type is event sourced.
/// </summary>
/// <remarks>
/// One place, deliberately. The routing decision must not appear in six endpoints: six copies of
/// "is this type event sourced" drift, and the drift is invisible because both branches produce a
/// valid-looking document. <c>IContentWriter</c> is the only caller on the write path.
/// </remarks>
public interface IContentSourcingPolicy
{
    /// <summary>The standing decision for a type name, or null when none was ever recorded.</summary>
    Task<ContentTypeSourcingPolicy?> GetAsync(string contentTypeName, CancellationToken cancellationToken);

    /// <summary>Is the stream the source of truth for this type? False when no policy exists.</summary>
    Task<bool> IsEventSourcedAsync(string contentTypeName, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the standing decision for a name, recording <paramref name="eventSourced"/> as it if
    /// there is none.
    /// </summary>
    /// <remarks>
    /// Never overwrites. A caller that gets back a policy disagreeing with what it asked for is
    /// looking at a name that was decided before, and its own request has to be refused rather than
    /// applied. The write is staged into the caller's session and committed with it.
    /// </remarks>
    Task<ContentTypeSourcingPolicy> DecideAsync(string contentTypeName, bool eventSourced, CancellationToken cancellationToken);
}
