namespace BarakoCMS.Files.Storage;

/// <summary>
/// Where one tenant's new uploads are stored, set through <c>/api/settings/storage</c>. One per
/// tenant, under <see cref="SingletonId"/>; documents are tenant-scoped, so each tenant has its own.
/// </summary>
/// <remarks>
/// The S3 block is kept when the provider is switched back to Postgres, because files already
/// stored in the bucket are still read through it: reads follow each file's own provider, not this
/// setting.
/// </remarks>
public sealed class StorageSettings
{
    public const string SingletonId = "storage";

    public string Id { get; set; } = SingletonId;

    /// <summary><c>postgres</c> or <c>s3</c>: where new uploads go.</summary>
    public string Provider { get; set; } = StorageProviders.Postgres;

    public ObjectStorageSettings? S3 { get; set; }

    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

/// <summary>An S3-compatible bucket and the credentials for it.</summary>
public sealed class ObjectStorageSettings
{
    public string Bucket { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-1";
    public string? ServiceUrl { get; set; }
    public string? PublicBaseUrl { get; set; }
    public bool UsePublicReadAcl { get; set; }
    public bool ForcePathStyle { get; set; } = true;
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>The secret key, encrypted with the deployment's secret protector. Never returned.</summary>
    public string? ProtectedSecretKey { get; set; }

    public ObjectStorageSettings Copy() => (ObjectStorageSettings)MemberwiseClone();
}

public static class StorageProviders
{
    public const string Postgres = "postgres";
    public const string S3 = "s3";
}
