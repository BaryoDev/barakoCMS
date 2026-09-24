namespace barakoCMS.Features.Collections.Push;

/// <summary>How much one push may carry.</summary>
/// <remarks>
/// An outside party calls this, so both are bounded. A thousand entries covers a changelog with
/// every release since 1.0 or a docs site several times the size of barakocms.com's, and a push
/// with <c>archiveMissing</c> has to carry the whole collection in one request.
/// </remarks>
internal static class Limits
{
    public const int MaxEntries = 1000;

    public const long MaxBodyBytes = 4L * 1024 * 1024;

    /// <summary>The most entries one push may archive, so a push that names almost nothing cannot empty a large collection in one request.</summary>
    public const int MaxArchived = 1000;
}
