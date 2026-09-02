namespace barakoCMS.Models;

/// <summary>
/// Defines all valid workflow trigger events.
/// </summary>
public static class WorkflowEvents
{
    /// <summary>
    /// Triggered when new content is created.
    /// </summary>
    public const string Created = "Created";
    
    /// <summary>
    /// Triggered when existing content is updated.
    /// </summary>
    public const string Updated = "Updated";
    
    /// <summary>
    /// Triggered when content is deleted.
    /// </summary>
    public const string Deleted = "Deleted";
    
    /// <summary>
    /// Triggered when content status changes to Published.
    /// </summary>
    public const string Published = "Published";
    
    /// <summary>
    /// The prefix a trigger uses to name a lifecycle transition rather than one of the four events
    /// above, as in "transition:Approve".
    /// </summary>
    /// <remarks>
    /// Prefixed rather than accepted as a bare name so a transition can never be confused with a
    /// built-in trigger. A type declaring a transition called "Published" would otherwise fire on
    /// the status change as well, and nothing about that would look wrong.
    ///
    /// This is deliberately its own constant and not shared with the permission action prefix, which
    /// happens to be spelled the same way. One names an action a role may be granted and the other
    /// names an event a workflow listens for; making them the same symbol would mean a change to
    /// either silently rewrites the other.
    /// </remarks>
    public const string TransitionPrefix = "transition:";

    /// <summary>
    /// Gets all valid trigger event names. Transitions are named per content type and are not
    /// listed here.
    /// </summary>
    /// <returns>Array of all valid event type strings.</returns>
    public static string[] All => new[] { Created, Updated, Deleted, Published };

    /// <summary>Whether this trigger names a lifecycle transition.</summary>
    public static bool IsTransition(string eventName) =>
        eventName is not null && eventName.StartsWith(TransitionPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The transition named by a trigger, or null if it does not name one.
    /// </summary>
    public static string? TransitionName(string eventName) =>
        IsTransition(eventName) && eventName.Length > TransitionPrefix.Length
            ? eventName[TransitionPrefix.Length..]
            : null;

    /// <summary>The trigger a workflow stores to fire on a named transition.</summary>
    public static string ForTransition(string transitionName) => TransitionPrefix + transitionName;

    /// <summary>
    /// Checks if the given event name is well formed.
    /// </summary>
    /// <remarks>
    /// Well formed is all this can answer for a transition. Whether the named transition actually
    /// exists depends on the triggering content type's lifecycle, which this cannot see, and that
    /// check lives in <c>WorkflowSchemaValidator.ValidateAsync</c>. Answering true here for
    /// "transition:" plus anything is only safe because that second check refuses an undeclared
    /// name; a workflow that saves and never fires looks exactly like one that fires and fails.
    /// </remarks>
    /// <param name="eventName">The event name to check.</param>
    /// <returns>True if the event name is valid; otherwise false.</returns>
    public static bool IsValid(string eventName)
    {
        return All.Contains(eventName) || TransitionName(eventName) is { Length: > 0 };
    }
}
