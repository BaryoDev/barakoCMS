using BarakoCMS.Files.Storage;
using Microsoft.AspNetCore.Http;

namespace BarakoCMS.Files;

/// <summary>What a download route sends for a stored file: a redirect, the bytes, or nothing found.</summary>
/// <param name="RedirectUrl">The object's public URL when the store serves it.</param>
/// <param name="Bytes">The stored bytes, null when there is no redirect and the storage has no object.</param>
/// <param name="ContentType">The type to send the bytes as.</param>
internal sealed record FileServe(string? RedirectUrl, byte[]? Bytes, string ContentType);

/// <summary>The part of serving a stored file that the authenticated and the public download routes share.</summary>
internal static class FileServing
{
    /// <summary>Lets a front end on another origin load the file.</summary>
    public static void AllowCrossOrigin(HttpContext http)
    {
        // A front end on another origin loads these as images and media, so the global
        // same-origin policy would break them. The security-headers middleware keeps a value set here.
        // A 304 must carry it too: a browser merges its headers into the cached response.
        http.Response.Headers["Cross-Origin-Resource-Policy"] = "cross-origin";
    }

    /// <summary>
    /// Sets the safety headers, then decides between the object's public URL and the stored bytes.
    /// </summary>
    /// <remarks>
    /// A file stored before uploads were checked against their bytes carries whatever type its client
    /// declared, so the browser must neither sniff a different one nor run script from a document
    /// opened directly on this origin. Only a type the upload check would accept goes to the store,
    /// which serves it without these headers; anything else is an old row, streamed from here as a
    /// plain download.
    /// </remarks>
    public static async Task<FileServe> PrepareAsync(
        HttpContext http, StoredFile served, bool checkedType, FileStorageResolver storage, CancellationToken ct)
    {
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        AllowCrossOrigin(http);

        var contentType = checkedType ? served.ContentType : "application/octet-stream";
        if (checkedType && !string.IsNullOrEmpty(served.PublicUrl))
        {
            return new FileServe(served.PublicUrl, null, contentType);
        }

        var bytes = await (await storage.ForAsync(served, ct)).GetAsync(served.StorageKey, ct);
        return new FileServe(null, bytes, contentType);
    }
}
