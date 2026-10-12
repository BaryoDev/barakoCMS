using barakoCMS.Models;
using FastEndpoints;
using FluentValidation;

namespace barakoCMS.Features.ContentType.ChangeField;

/// <summary>One change to one field. Send one of newName, toType or remove, or only displayName.</summary>
internal sealed class Request
{
    /// <summary>A new label. On its own, applied at once: no entry changes.</summary>
    public string? DisplayName { get; set; }

    /// <summary>A new name. Every entry's value moves to it, by a job.</summary>
    public string? NewName { get; set; }
}

internal sealed class Response
{
    /// <summary><c>additive</c>, applied at once, or <c>rename</c>, applied by the job in <see cref="Change"/>.</summary>
    public string Classification { get; init; } = string.Empty;

    /// <summary>The job applying the change, or null for a change applied at once.</summary>
    public FieldChangeResponse? Change { get; init; }
}

/// <summary>A field change as it stands.</summary>
internal sealed class FieldChangeResponse
{
    public Guid Id { get; init; }
    public string ContentType { get; init; } = string.Empty;
    public string Field { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;

    /// <summary>The step it is on, from 0, of <see cref="Steps"/>.</summary>
    public int Step { get; init; }

    public int Steps { get; init; }

    /// <summary>Entries rewritten so far.</summary>
    public int Processed { get; init; }

    /// <summary>Entries of the type when the change was asked for.</summary>
    public int Total { get; init; }

    /// <summary>Entries whose value the change drops, counted when it was asked for.</summary>
    public int ValuesLost { get; init; }

    /// <summary>The last failure, cleared once a batch commits again.</summary>
    public string? Error { get; init; }

    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    public static FieldChangeResponse From(FieldChange c) => new()
    {
        Id = c.Id,
        ContentType = c.ContentType,
        Field = c.Field,
        Kind = c.Kind.ToString().ToLowerInvariant(),
        State = c.State.ToString().ToLowerInvariant(),
        Step = c.Step,
        Steps = barakoCMS.Infrastructure.Jobs.FieldChanges.Plan(c).Count,
        Processed = c.Processed,
        Total = c.Total,
        ValuesLost = c.ValuesLost,
        Error = c.Error,
        StartedAt = c.StartedAt,
        CompletedAt = c.CompletedAt,
    };
}

internal sealed class Validator : Validator<Request>
{
    public Validator()
    {
        RuleFor(r => r.DisplayName).MaximumLength(200);
        RuleFor(r => r.NewName).MaximumLength(100);
    }
}
