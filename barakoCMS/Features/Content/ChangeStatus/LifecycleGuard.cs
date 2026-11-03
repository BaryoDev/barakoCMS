namespace barakoCMS.Features.Content.ChangeStatus;

internal static class LifecycleGuard
{
    /// <summary>
    /// The refusal for setting a status by hand on a type with a lifecycle. Such a type changes status
    /// through a named transition, and the status field would otherwise step around it.
    /// </summary>
    public static string StatusRefusal(string contentType) =>
        $"Content type '{contentType}' declares a lifecycle, so it changes status through a Transition, not Status.";
}
