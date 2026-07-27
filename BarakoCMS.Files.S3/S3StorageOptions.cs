namespace BarakoCMS.Files.S3;

/// <summary>
/// Configuration for the S3-compatible storage provider, bound from the <c>Files:S3</c> config section.
/// The same options drive AWS S3, Cloudflare R2, and MinIO; only <see cref="ServiceUrl"/> and
/// <see cref="PublicBaseUrl"/> differ between them.
/// </summary>
public sealed class S3StorageOptions
{
    /// <summary>The bucket to store objects in.</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>
    /// The S3 endpoint for R2 or MinIO (e.g. <c>https://&lt;account&gt;.r2.cloudflarestorage.com</c> or
    /// <c>http://localhost:9000</c>). Leave null for AWS S3, which is reached via <see cref="Region"/>.
    /// </summary>
    public string? ServiceUrl { get; set; }

    /// <summary>AWS region, used only when <see cref="ServiceUrl"/> is null.</summary>
    public string Region { get; set; } = "us-east-1";

    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Path-style addressing. Required for MinIO and R2; harmless for AWS.</summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>
    /// The public base URL a public object is reachable at (a bucket public URL, an R2 public bucket /
    /// custom domain, or a CDN in front). Combined with the object key to form the file's public URL.
    /// If null, public files fall back to being proxied through the API.
    /// </summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>
    /// Set a public-read ACL on public objects (AWS and MinIO honor it). Cloudflare R2 ignores object
    /// ACLs — make the bucket public in the R2 dashboard instead and set this false.
    ///
    /// <para>Caveat with a public bucket (the R2 setup): every object in it is readable by anyone who
    /// knows the key, so a "private" file physically resides in public space and is protected only by
    /// its unguessable key (the app never discloses a private file's key or URL). With AWS or MinIO the
    /// per-object ACL keeps private objects genuinely private. If you need strict private files on R2,
    /// use a separate private bucket for them.</para>
    /// </summary>
    public bool UsePublicReadAcl { get; set; } = true;
}
