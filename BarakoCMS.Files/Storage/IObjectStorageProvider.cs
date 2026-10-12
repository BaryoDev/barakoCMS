namespace BarakoCMS.Files.Storage;

/// <summary>
/// Builds storage from a tenant's settings, for a provider module such as <c>BarakoCMS.Files.S3</c>.
/// Files cannot reference the provider modules, so they register one of these instead.
/// </summary>
public interface IObjectStorageProvider
{
    /// <summary>The provider id stored on each file this storage writes, such as <c>s3</c>.</summary>
    string Provider { get; }

    /// <summary>The storage for these settings, with the secret already decrypted.</summary>
    IFileStorage Create(ObjectStorageSettings settings, string secretKey);

    /// <summary>
    /// Puts, reads back and deletes one small object with these settings, and says which step
    /// failed. Never throws for a provider error: that is the answer.
    /// </summary>
    Task<StorageTestResult> TestAsync(ObjectStorageSettings settings, string secretKey, CancellationToken ct);
}

/// <param name="Ok">Whether all three steps worked.</param>
/// <param name="Step">The step that failed: <c>put</c>, <c>get</c> or <c>delete</c>. Null when it worked.</param>
/// <param name="ErrorCode">The provider's own error code, such as <c>InvalidAccessKeyId</c>, when it gave one.</param>
/// <param name="Message">What went wrong, in the provider's words, or that it worked.</param>
public sealed record StorageTestResult(bool Ok, string? Step, string? ErrorCode, string Message);

/// <summary>
/// The storage settings a deployment sets in configuration (<c>Files:S3</c>, <c>FILES__S3__*</c>).
/// Any of them makes the API's settings read-only, and the configured storage serves every tenant.
/// </summary>
public interface IStorageEnvironment
{
    /// <summary>The keys set in configuration, such as <c>Bucket</c> and <c>Region</c>. Empty when none is.</summary>
    IReadOnlyList<string> Keys { get; }
}

internal sealed class NoStorageEnvironment : IStorageEnvironment
{
    public IReadOnlyList<string> Keys => [];
}
