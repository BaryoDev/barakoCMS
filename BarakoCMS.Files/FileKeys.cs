namespace BarakoCMS.Files;

/// <summary>
/// Where an object sits in storage: under <c>public/</c> or <c>private/</c>, by the file's
/// visibility.
/// </summary>
/// <remarks>
/// A bucket policy and a CDN origin grant by key prefix, not by anything in the database, so the
/// prefix is the only thing in front of the bucket that can tell a public file from a private one.
/// A file stored before the prefixes existed keeps the key on its row and is read, resized and
/// deleted by that key. Compared ordinally, because an object store's keys are case sensitive and
/// so is a policy on <c>public/*</c>.
/// </remarks>
public static class FileKeys
{
    public const string PublicPrefix = "public/";
    public const string PrivatePrefix = "private/";

    public static string Prefix(bool isPublic) => isPublic ? PublicPrefix : PrivatePrefix;

    /// <summary>
    /// Whether <paramref name="key"/> sits under the prefix of the other visibility. A key under
    /// neither prefix contradicts nothing: that is every file stored before the prefixes existed.
    /// </summary>
    public static bool Contradicts(string key, bool isPublic) =>
        key.StartsWith(Prefix(!isPublic), StringComparison.Ordinal);

    /// <summary>
    /// The key for a new upload. Nothing in it comes from the request: the name is random and the
    /// extension is the one the checked content type maps to.
    /// </summary>
    internal static string ForUpload(bool isPublic, string extension) =>
        $"{Prefix(isPublic)}{Guid.NewGuid():N}{extension}";

    /// <summary>The key for a resize of <paramref name="original"/>.</summary>
    /// <remarks>
    /// The prefix comes from the original's flag and only the last segment of its key is carried
    /// over, so a resize lands under the right prefix whatever the original's key looks like: one
    /// stored before the prefixes existed, or one a host wrote itself.
    /// </remarks>
    internal static string ForVariant(StoredFile original, int width) =>
        Prefix(original.IsPublic) + ImageVariants.VariantKey(Path.GetFileName(original.StorageKey), width);
}
