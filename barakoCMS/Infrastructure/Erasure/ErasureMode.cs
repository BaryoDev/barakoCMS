namespace barakoCMS.Infrastructure.Erasure;

/// <summary>
/// How this deployment answers an erasure request against event-sourced content. Set with
/// <c>Erasure:Mode</c>. See DECISIONS.md D9.
/// </summary>
public enum ErasureMode
{
    /// <summary>
    /// Erasing a content item removes its events, its stream and its read-model document, in one
    /// transaction. The item's history goes with it, which is what erasure means. The default,
    /// because it is the only mode that works on data already written.
    /// </summary>
    Delete = 0,

    /// <summary>
    /// Event payloads are encrypted per subject and erasure destroys the key. Not yet available:
    /// D9 records that a CMS has no natural data subject, and that question has to be answered
    /// before this can do anything. Selecting it fails at startup rather than silently behaving
    /// like something else.
    /// </summary>
    CryptoShred = 1,

    /// <summary>
    /// No erasure path at all: the stream stays append-only. A legitimate choice for a deployment
    /// that has decided its content never holds personal data, and one that has to be made on
    /// purpose, so it requires <c>Erasure:AcknowledgeNoErasure</c>.
    /// </summary>
    None = 2,
}
