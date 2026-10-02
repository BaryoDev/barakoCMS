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
}
