namespace barakoCMS.Features.ContentType.SetPublicDelivery;

internal class Request
{
    /// <summary>True to serve this type from the anonymous public delivery API.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Confirms that turning delivery on exposes every published entry of this type to anonymous
    /// callers. Only consulted when <c>PublicDelivery:RequireAcknowledgement</c> is on, and only
    /// when enabling.
    /// </summary>
    /// <remarks>
    /// Off by default, which is what this endpoint has always done. It is the documented way back
    /// from the opt-in migration that stopped delivering every existing type, so making the recovery
    /// path refuse the request until a client is updated would be the wrong trade for a deployment
    /// that just wants its blog serving again. A deployment that wants the ceremony turns it on.
    /// </remarks>
    public bool AcknowledgeExposure { get; set; }
}

internal class Response
{
    public string Name { get; set; } = string.Empty;
    public bool IsPubliclyDeliverable { get; set; }

    /// <summary>How many published entries this changed the reach of.</summary>
    /// <remarks>
    /// The same number the audit entry records. Reported back so the admin can say what happened
    /// rather than only that something did, which is the difference between "public delivery
    /// enabled" and "public delivery enabled, 4,000 entries now anonymous".
    /// </remarks>
    public int PublishedEntries { get; set; }
}
