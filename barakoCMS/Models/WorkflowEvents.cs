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
    /// Gets all valid trigger event names.
    /// </summary>
    /// <returns>Array of all valid event type strings.</returns>
    public static string[] All => new[] { Created, Updated, Deleted, Published };
    
    /// <summary>
    /// Checks if the given event name is valid.
    /// </summary>
    /// <param name="eventName">The event name to check.</param>
    /// <returns>True if the event name is valid; otherwise false.</returns>
    public static bool IsValid(string eventName)
    {
        return All.Contains(eventName);
    }
}
