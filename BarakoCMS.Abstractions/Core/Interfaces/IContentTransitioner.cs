using barakoCMS.Models;

namespace barakoCMS.Core.Interfaces;

/// <summary>
/// The single way an entry moves through its content type's lifecycle.
/// </summary>
/// <remarks>
/// The change-status endpoint is one caller. A webhook, a scheduled job or a module is another, and
/// each gets the rules the endpoint applies: the read floor, the transition permission, the self
/// transition rule, the lifecycle's own from-state, the fields the transition requires, and the
/// same events and audit row.
///
/// Unlike <see cref="IContentWriter"/> this commits. A stale write is only known at the commit, and
/// the answer to it is part of the result. Anything the caller staged in the scope's session before
/// the call is committed with the move, and after a <see cref="ContentTransitionOutcome.Conflict"/>
/// the session still holds what was staged, so the caller should not reuse it.
/// </remarks>
public interface IContentTransitioner
{
    /// <summary>Performs a named transition on an entry, as the given actor.</summary>
    /// <param name="content">
    /// The entry, loaded by the caller from this scope's session, in the tenant of the scope.
    /// </param>
    /// <param name="transition">The transition's name, as the type declares it, ignoring case.</param>
    /// <param name="actor">Who is making the move. Recorded on the events and the audit row.</param>
    /// <param name="options">Values sent with the move, and the checks the caller takes on itself.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ContentTransitionResult> TransitionAsync(
        Content content,
        string transition,
        ContentTransitionActor actor,
        ContentTransitionOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Who is making a transition: a user, or code acting under its own name.
/// </summary>
/// <remarks>
/// A user actor is checked through the permission resolver exactly as a caller of the endpoint is.
/// An API key acts as the user who owns it, so it is a user actor too.
///
/// A system actor is not a user and holds no permissions, so on its own it is refused. The caller
/// has to set <see cref="ContentTransitionOptions.SkipPermissionChecks"/> to make the move on its
/// own authority, which keeps that decision visible at the call site.
/// </remarks>
public sealed class ContentTransitionActor
{
    /// <summary>The longest name a system actor may carry.</summary>
    public const int MaxSystemNameLength = 64;

    private ContentTransitionActor(Guid? userId, string? systemName)
    {
        UserId = userId;
        SystemName = systemName;
    }

    /// <summary>The user making the move, or null for a system actor.</summary>
    public Guid? UserId { get; }

    /// <summary>The name of the system actor, or null for a user.</summary>
    public string? SystemName { get; }

    public bool IsSystem => UserId is null;

    /// <summary>
    /// The id the events record: the user's, or <see cref="Guid.Empty"/> for a system actor, which
    /// is what the scheduler and collection syncs already record for a write no user made.
    /// </summary>
    public Guid RecordedId => UserId ?? Guid.Empty;

    /// <summary>A user, by id. The user has to exist when the move is made.</summary>
    public static ContentTransitionActor ForUser(Guid userId)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("A user actor needs a user id. Guid.Empty is what a system actor records.", nameof(userId));

        return new ContentTransitionActor(userId, null);
    }

    /// <summary>Code acting under its own name, such as <c>payments-webhook</c>.</summary>
    /// <param name="name">
    /// Letters, digits, dot, dash and underscore, up to <see cref="MaxSystemNameLength"/>
    /// characters. It is written to the audit log, so it is a fixed name and not a value from a
    /// request.
    /// </param>
    public static ContentTransitionActor ForSystem(string name)
    {
        if (string.IsNullOrEmpty(name)
            || name.Length > MaxSystemNameLength
            || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
        {
            throw new ArgumentException(
                $"A system actor's name is 1 to {MaxSystemNameLength} letters, digits, dots, dashes or underscores.",
                nameof(name));
        }

        return new ContentTransitionActor(null, name);
    }
}

/// <summary>What a caller sends with a transition, and which checks it takes on itself.</summary>
public sealed class ContentTransitionOptions
{
    /// <summary>
    /// Values for the fields the transition declares, written with the move. Ignored by a
    /// transition that declares none.
    /// </summary>
    public IReadOnlyDictionary<string, object>? Data { get; init; }

    /// <summary>
    /// The caller has authorised this move itself, so the actor's permissions are not consulted.
    /// Off by default.
    /// </summary>
    /// <remarks>
    /// Skips the three answers that depend on what the actor may do: read on the content type, the
    /// permission on the transition, and field sensitivity on the values in <see cref="Data"/>.
    /// Everything else still applies: a user actor has to exist and may not move on an entry they
    /// created unless configuration allows it, the transition has to be declared and start from
    /// the entry's state, required fields have to be sent, and the values are validated and run
    /// through the before-save hooks. The audit row records that the checks were skipped.
    /// </remarks>
    public bool SkipPermissionChecks { get; init; }
}

public enum ContentTransitionOutcome
{
    /// <summary>The entry moved, and the move is committed.</summary>
    Transitioned,

    /// <summary>The actor may not make this move. Nothing was written.</summary>
    Forbidden,

    /// <summary>
    /// The request is wrong: no such transition, a required field not sent, or a value the type
    /// refuses. Nothing was written.
    /// </summary>
    Invalid,

    /// <summary>
    /// The entry is not in the state the transition starts from, or another writer changed it
    /// first. Nothing was written.
    /// </summary>
    Conflict,
}

/// <summary>What became of a transition.</summary>
public sealed class ContentTransitionResult
{
    private ContentTransitionResult(
        ContentTransitionOutcome outcome,
        IReadOnlyList<string> errors,
        string? transition = null,
        string? fromState = null,
        string? toState = null)
    {
        Outcome = outcome;
        Errors = errors;
        Transition = transition;
        FromState = fromState;
        ToState = toState;
    }

    public ContentTransitionOutcome Outcome { get; }

    public bool Succeeded => Outcome == ContentTransitionOutcome.Transitioned;

    /// <summary>
    /// Why the move was refused, in order, and empty when it was made. A
    /// <see cref="ContentTransitionOutcome.Forbidden"/> reason is for the calling code and its log.
    /// The endpoint answers 403 with no body, so a caller without the right learns nothing about
    /// the type's transitions or the entry's state.
    /// </summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>The transition's name as the type declares it. Set when the move was made.</summary>
    public string? Transition { get; }

    /// <summary>The state the entry was in. Set when the move was made.</summary>
    public string? FromState { get; }

    /// <summary>The state the entry is in now. Set when the move was made.</summary>
    public string? ToState { get; }

    public static ContentTransitionResult Transitioned(string transition, string fromState, string toState) =>
        new(ContentTransitionOutcome.Transitioned, [], transition, fromState, toState);

    public static ContentTransitionResult Forbidden(string reason) =>
        new(ContentTransitionOutcome.Forbidden, [reason]);

    public static ContentTransitionResult Invalid(IReadOnlyList<string> errors) =>
        new(ContentTransitionOutcome.Invalid, [.. errors]);

    public static ContentTransitionResult Conflict(string reason) =>
        new(ContentTransitionOutcome.Conflict, [reason]);
}
