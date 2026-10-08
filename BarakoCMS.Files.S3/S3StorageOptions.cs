namespace BarakoCMS.Files.S3;

/// <summary>
/// Configuration for the S3-compatible storage provider, bound from the <c>Files:S3</c> config section.
/// The same options drive AWS S3, Cloudflare R2, and self-hosted S3-compatible stores; only <see cref="ServiceUrl"/> and
/// <see cref="PublicBaseUrl"/> differ between them.
/// </summary>
public sealed class S3StorageOptions
{
    /// <summary>The bucket to store objects in.</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>
    /// The S3 endpoint for R2 or a self-hosted store (e.g. <c>https://&lt;account&gt;.r2.cloudflarestorage.com</c> or
    /// <c>http://localhost:9000</c>). Leave null for AWS S3, which is reached via <see cref="Region"/>.
    /// </summary>
    public string? ServiceUrl { get; set; }

    /// <summary>AWS region, used only when <see cref="ServiceUrl"/> is null.</summary>
    public string Region { get; set; } = "us-east-1";

    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Path-style addressing. Required for most self-hosted stores and R2; harmless for AWS.</summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>
    /// The public base URL a public object is reachable at: the URL of a bucket that grants anonymous
    /// read on <c>public/*</c>, or a CDN in front of it. The object key, which already starts with
    /// <c>public/</c>, is appended to form the file's public URL, so this is the root of the bucket
    /// or of the CDN and never a path ending in <c>/public</c>. If null, public files fall back to
    /// being proxied through the API.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// Set a public-read ACL on public objects. AWS applies it on a bucket with ACLs enabled. Cloudflare
    /// R2, Garage and SeaweedFS do not apply an ACL sent with an upload, so set this false there.
    ///
    /// <para>Do not make the bucket public as a whole instead: every object in it is then readable by
    /// anyone who knows the key, private files included. Public files are stored under
    /// <c>public/</c> and private ones under <c>private/</c>, so grant anonymous read on
    /// <c>public/*</c> only, or let a CDN read only that prefix while its origin path stays empty,
    /// so the URL path is the key. Where the store offers no grant narrower than the bucket, leave
    /// <see cref="PublicBaseUrl"/> null and public files are served through the API. A file stored
    /// before the prefixes existed sits at the bucket root whatever its visibility, and a grant on
    /// <c>public/*</c> does not cover it.</para>
    /// </summary>
    public bool UsePublicReadAcl { get; set; } = true;

    public const int MaxErrorRetryLimit = 10;

    /// <summary>
    /// Tries after the first that the SDK makes for one call. The SDK's own default is four, which
    /// with its default timeout could run one call past the lease of the job it runs in.
    /// </summary>
    public int MaxErrorRetry { get; set; } = 2;

    /// <summary>How long one try may take, in seconds, a body upload included.</summary>
    public double TimeoutSeconds { get; set; } = 45;

    /// <summary>
    /// The longest the SDK waits between two tries. Legacy retry mode, the SDK's default, caps each
    /// wait at 30 s and standard mode at 20 s, so the larger is assumed whichever is in use.
    /// </summary>
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    /// <summary>The workflow runner's lease, which the core does not expose to a module. Pinned by a test.</summary>
    internal static readonly TimeSpan WorkflowLease = TimeSpan.FromMinutes(5);

    /// <summary>The share of the shortest lease one call may use, the same share outbound calls get.</summary>
    internal const double LeaseShare = 0.8;

    internal const string JobLeaseSecondsKey = "Jobs:LeaseSeconds";
    internal const int DefaultJobLeaseSeconds = 600;

    internal TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);

    /// <summary>The longest one S3 call can take: every try running to its timeout, every wait at its ceiling.</summary>
    internal TimeSpan MaxCallDuration => Timeout * (MaxErrorRetry + 1) + MaxBackoff * MaxErrorRetry;

    /// <summary>
    /// Why these settings cannot be used, or null. Refuses a value out of range, and settings that let
    /// one call use more than <see cref="LeaseShare"/> of the shorter of the workflow runner's lease
    /// and <c>Jobs:LeaseSeconds</c>. A file read inside a job or a workflow action that runs past its
    /// lease is run again by another node.
    /// </summary>
    internal string? Problem(int jobLeaseSeconds)
    {
        if (MaxErrorRetry is < 0 or > MaxErrorRetryLimit)
            return $"Modules:Files.S3:MaxErrorRetry must be between 0 and {MaxErrorRetryLimit}.";
        if (!(TimeoutSeconds > 0))
            return "Modules:Files.S3:TimeoutSeconds must be positive.";
        if (jobLeaseSeconds < 1)
            return null;

        var jobs = TimeSpan.FromSeconds(jobLeaseSeconds);
        var lease = jobs < WorkflowLease ? jobs : WorkflowLease;
        if (MaxCallDuration <= lease * LeaseShare)
            return null;

        return $"The Modules:Files.S3 settings allow one S3 call to take up to {MaxCallDuration.TotalSeconds:0.#} s "
            + $"({MaxErrorRetry + 1} tries of {TimeoutSeconds:0.#} s and up to {MaxBackoff.TotalSeconds:0} s between them), "
            + $"which is more than {LeaseShare:P0} of the shortest lease ({lease.TotalSeconds:0.#} s, the lower of the "
            + "workflow runner's 300 s and Jobs:LeaseSeconds). A call past its lease is run again by another node. "
            + "Lower MaxErrorRetry or TimeoutSeconds, or raise Jobs:LeaseSeconds.";
    }
}
