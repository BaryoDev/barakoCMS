using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Files.S3;

/// <summary>
/// Stores file bytes in an S3-compatible object store (AWS S3, Cloudflare R2, MinIO). Public files are
/// written public-read (where the store honors ACLs) and get a direct <see cref="PublicUrl"/> a browser
/// can use as an image src; private files return null and are proxied through the API. Metadata stays
/// in Postgres (a StoredFile record); only the bytes live here.
/// </summary>
public sealed class S3FileStorage : IFileStorage
{
    private readonly IAmazonS3 _s3;
    private readonly S3StorageOptions _opts;

    public S3FileStorage(IAmazonS3 s3, IOptions<S3StorageOptions> opts)
    {
        _s3 = s3;
        _opts = opts.Value;
    }

    public string Provider => "s3";

    public async Task<StoredObjectRef> PutAsync(Stream content, string key, string contentType, bool isPublic, CancellationToken ct = default)
    {
        /* Buffer to a seekable stream so the SDK knows the length up front — avoids chunked-signing
         * issues against MinIO/R2 and works for non-seekable upload streams. */
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        var put = new PutObjectRequest
        {
            BucketName = _opts.Bucket,
            Key = key,
            InputStream = buffer,
            ContentType = contentType,
        };
        if (isPublic && _opts.UsePublicReadAcl)
            put.CannedACL = S3CannedACL.PublicRead;

        await _s3.PutObjectAsync(put, ct);
        return new StoredObjectRef(key, PublicUrl(key, isPublic));
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _s3.GetObjectAsync(_opts.Bucket, key, ct);
            using var ms = new MemoryStream();
            await resp.ResponseStream.CopyToAsync(ms, ct);
            return ms.ToArray();
        }
        catch (AmazonS3Exception e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public string? PublicUrl(string key, bool isPublic)
    {
        if (!isPublic || string.IsNullOrEmpty(_opts.PublicBaseUrl)) return null;
        return $"{_opts.PublicBaseUrl!.TrimEnd('/')}/{key}";
    }

    public Task DeleteAsync(string key, CancellationToken ct = default) =>
        _s3.DeleteObjectAsync(_opts.Bucket, key, ct);
}
