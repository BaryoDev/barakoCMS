namespace barakoCMS.Features.Content.Batch;

internal static class BatchOps
{
    public const string Create = "create";
    public const string Transition = "transition";
    public const int MaxSteps = 100;
}

/// <summary>One write of a batch: a create, or a lifecycle transition.</summary>
internal sealed class BatchStep
{
    /// <summary><c>create</c> or <c>transition</c>.</summary>
    public string Op { get; set; } = "";

    /// <summary>Create only: the type of the new entry.</summary>
    public string ContentType { get; set; } = "";

    /// <summary>The id of the new entry, made by the client, or the id of the entry to move.</summary>
    public Guid Id { get; set; }

    /// <summary>Create: the field values. Transition: the values the transition declares.</summary>
    public Dictionary<string, object>? Data { get; set; }

    /// <summary>Create only, optional: a <c>ContentStatus</c> name. Draft when left out.</summary>
    public string? Status { get; set; }

    /// <summary>Create only, optional: a <c>SensitivityLevel</c> name. Public when left out.</summary>
    public string? Sensitivity { get; set; }

    /// <summary>Transition only: the name of the transition to make.</summary>
    public string? Transition { get; set; }
}

internal sealed class BatchRequest
{
    public List<BatchStep> Steps { get; set; } = [];
}

/// <summary>What one step did. <paramref name="State"/> is the lifecycle state after it, or null for a type with none.</summary>
internal sealed record BatchStepResult(int Index, string Op, Guid Id, string? State);

internal sealed record BatchResponse(List<BatchStepResult> Steps);
