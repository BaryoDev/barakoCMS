namespace barakoCMS.Models;

public class WorkflowDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TriggerContentType { get; set; } = string.Empty; // e.g., "PurchaseOrder"

    /// <summary>
    /// More content types this workflow fires for, alongside <see cref="TriggerContentType"/>.
    /// </summary>
    /// <remarks>
    /// The workflow fires for <see cref="TriggerContentType"/> and for every entry here, so neither
    /// field overrides the other. A workflow saved before this existed has no list and fires for its
    /// single type, as it always did. Saving through the API stores both: the list holds every type
    /// and <see cref="TriggerContentType"/> holds the first, so a reader that only knows the single
    /// field still sees a type the workflow fires for.
    /// </remarks>
    public List<string> TriggerContentTypes { get; set; } = new();

    public string TriggerEvent { get; set; } = string.Empty; // e.g., "Created", "Updated"

    /// <summary>
    /// More events this workflow fires on, alongside <see cref="TriggerEvent"/>.
    /// </summary>
    /// <remarks>
    /// Read the same way as <see cref="TriggerContentTypes"/>: the workflow fires on
    /// <see cref="TriggerEvent"/> and on every entry here, a workflow saved before this existed has
    /// no list and fires on its single event, and saving through the API stores every event in the
    /// list and the first in <see cref="TriggerEvent"/>.
    /// </remarks>
    public List<string> TriggerEvents { get; set; } = new();
    public Dictionary<string, string> Conditions { get; set; } = new(); // e.g., "Status" == "Approved"
    public List<WorkflowAction> Actions { get; set; } = new();

    /// <summary>Whether the workflow fires. On unless it was switched off.</summary>
    /// <remarks>
    /// A definition stored before this existed has no such field and reads as on, so nothing stops
    /// firing on upgrade. Switched off, it starts no runs, and the runner cancels a run it had
    /// already queued when it reaches it, so switching it back on does not send what was waiting.
    /// </remarks>
    public bool Enabled { get; set; } = true;
}

public class WorkflowAction
{
    public string Type { get; set; } = string.Empty; // "Email", "SMS", "Webhook"
    public Dictionary<string, string> Parameters { get; set; } = new(); // e.g., "To": "admin@example.com"

    /// <summary>What a run does with the actions after this one when this one fails.</summary>
    /// <remarks>
    /// An action stored before this existed has no such field and reads as
    /// <see cref="WorkflowFailurePolicy.Continue"/>, which is what every run did before it.
    ///
    /// Nullable so a request that sends its optional fields as null is not refused. Null means
    /// Continue, the same as leaving it out.
    /// </remarks>
    public WorkflowFailurePolicy? OnFailure { get; set; } = WorkflowFailurePolicy.Continue;

    /// <summary>
    /// The outputs of this action that are kept on the run, by name. Nothing is kept when it is
    /// empty or null.
    /// </summary>
    /// <remarks>
    /// An allowlist, because an output is usually a third party's response, and the run record is
    /// stored and served over the API. A name not listed here is dropped when the outcome is
    /// recorded and never reaches the database. A later action reads a kept one as
    /// <c>{{actions.N.name}}</c>, N being this action's position counting from zero. Copied onto a
    /// run when it is queued, like the parameters.
    /// </remarks>
    public List<string>? Capture { get; set; }

    /// <summary>
    /// What undoes this action when the run is rolled back. Null when nothing can, which is the
    /// default and is reported as not compensated rather than as fine.
    /// </summary>
    /// <remarks>
    /// A run is rolled back when an action set to <see cref="WorkflowFailurePolicy.Halt"/> fails for
    /// good, if any action of the workflow declares a compensation, and when an operator abandons
    /// it. The actions that succeeded are undone one at a time, the last first. The compensation can
    /// read this action's captured outputs as <c>{{actions.N.name}}</c>, which is how it names the
    /// thing it is undoing. Copied onto a run when it is queued, like the parameters.
    /// </remarks>
    public WorkflowCompensation? Compensate { get; set; }
}

/// <summary>The action that undoes another one.</summary>
public class WorkflowCompensation
{
    /// <summary>An action type, as in <see cref="WorkflowAction.Type"/>. A Conditional is not accepted.</summary>
    public string Type { get; set; } = string.Empty;

    public Dictionary<string, string> Parameters { get; set; } = new();
}

/// <summary>Whether the actions after a failed one still run.</summary>
/// <remarks>
/// Per action, because both answers are right for their own case. Notifications are independent,
/// and skipping the tweet because the mail server was down is a surprise. A chain is not: filing
/// the document after the journal entry failed carries on as though it had succeeded.
///
/// Numbered explicitly. Marten stores an enum as its number, so a member goes on the end.
/// </remarks>
public enum WorkflowFailurePolicy
{
    /// <summary>The actions after this one run whatever happens to it.</summary>
    Continue = 0,

    /// <summary>
    /// Nothing after this action runs until it has succeeded. While it waits on a retry the actions
    /// after it wait too, and once it has failed for good, or ended Unknown, they are skipped.
    /// </summary>
    Halt = 1,
}
