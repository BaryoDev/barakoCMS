namespace barakoCMS.Models;

/// <summary>
/// Whether entries of one content type are event sourced. Written once for a type name, and never
/// changed or deleted.
/// </summary>
/// <remarks>
/// Keyed by the content type NAME rather than carried on <see cref="ContentTypeDefinition"/>, and
/// that is the whole point of it being a separate document. On the definition the choice has a hole
/// in it: delete the type, recreate it with the same name and the opposite answer, and the streams
/// and documents already written belong to a type whose rules changed underneath them. Refusing to
/// delete a type that has content does not close it either, because deleting content and deleting a
/// stream are not the same operation. Keeping the decision outside the definition makes the hole
/// structurally impossible rather than rule-enforced: recreating a name finds the standing policy
/// and inherits it.
///
/// Absence means not event sourced, which is what every type created before this existed is, and
/// what a type is unless somebody says otherwise at creation.
///
/// There is no path that changes one of these, in either direction, and there is no endpoint that
/// deletes one. Turning it on later would mean synthesising a genesis event from the current
/// document, so the stream would assert a history that did not happen. Turning it off later would
/// discard the record callers rely on for audit. Both are unrecoverable, so neither is offered.
/// </remarks>
public class ContentTypeSourcingPolicy
{
    /// <summary>The normalised content type name. The identity of this document.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether the stream is the source of truth for entries of this type.</summary>
    public bool EventSourced { get; set; }

    /// <summary>When the decision was recorded, which is when the name was first created.</summary>
    public DateTimeOffset DecidedAt { get; set; } = DateTimeOffset.UtcNow;
}
