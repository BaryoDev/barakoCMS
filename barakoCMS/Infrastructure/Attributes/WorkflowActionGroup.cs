namespace barakoCMS.Infrastructure.Attributes;

/// <summary>
/// Where an action kind sits in the workflow builder's action library.
/// </summary>
/// <remarks>
/// <see cref="Unspecified"/> is the default so an action that predates this, or does not fit, still
/// loads. The API reports it as a null group rather than inventing one, and the console places it
/// under Other.
/// </remarks>
public enum WorkflowActionGroup
{
    Unspecified = 0,
    Content,
    Delivery,
    Comms,
    Data,
    Flow
}
