namespace BarakoCMS.Files;

/// <summary>
/// Where an object sits in storage: under <c>public/</c> or <c>private/</c>, by the file's
/// visibility.
/// </summary>
/// <remarks>
/// A bucket policy and a CDN origin grant by key prefix, not by anything in the database, so the
/// prefix is the only thing in front of the bucket that can tell a public file from a private one.
/// A file stored before the prefixes existed keeps the key on its row and is read, resized and
/// deleted by that key.
/// </remarks>
public static class FileKeys
{
    private const string PublicName = "public";
    private const string PrivateName = "private";

    public static string PublicPrefix => PublicName + "/";

    public static string PrivatePrefix => PrivateName + "/";

    public static string Prefix(bool isPublic) => isPublic ? PublicPrefix : PrivatePrefix;

    /// <summary>
    /// Whether <paramref name="key"/> could land under the prefix of the other visibility. A key
    /// under neither prefix contradicts nothing: that is every file stored before the prefixes
    /// existed.
    /// </summary>
    /// <remarks>
    /// Read the way the most forgiving store or proxy in front of one might read it, so the answer
    /// errs towards refusing: a leading slash, an empty segment and a <c>.</c> segment are dropped,
    /// <c>..</c> steps back one segment, a backslash separates like a slash, and the first segment
    /// left is compared without regard to case. AWS S3 and Cloudflare R2 keep keys exactly as sent
    /// and compare them case sensitively; that is not established here for every self-hosted store
    /// and every database one can keep its metadata in, and a private key refused for looking like
    /// <c>Public/x</c> costs nothing.
    /// </remarks>
    public static bool Contradicts(string key, bool isPublic) =>
        string.Equals(FirstSegment(key), isPublic ? PrivateName : PublicName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The key for a new upload. The name is random and the extension is the one the checked
    /// content type maps to, so the visibility flag is the only part a request decides.
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

    private static string? FirstSegment(string key)
    {
        var kept = new List<string>();

        foreach (var segment in key.Split('/', '\\'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (kept.Count > 0)
                {
                    kept.RemoveAt(kept.Count - 1);
                }

                continue;
            }

            kept.Add(segment);
        }

        return kept.Count > 0 ? kept[0] : null;
    }
}
