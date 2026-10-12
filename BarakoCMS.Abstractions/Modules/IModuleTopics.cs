namespace barakoCMS.Modules;

/// <summary>
/// Where a module names the realtime topics it offers: a group of its own event types, and the
/// capability a caller needs to read them through the change feed or the realtime hub.
/// </summary>
public interface IModuleTopics
{
    /// <param name="name">Lower case words joined by dots or dashes, for example <c>queue.tickets</c>. Unique across modules.</param>
    /// <param name="capability">What a caller must hold. Listed in GET /api/capabilities so roles can grant it.</param>
    /// <param name="eventTypes">One or more of the module's own event types.</param>
    void Add(string name, string capability, params Type[] eventTypes);
}
