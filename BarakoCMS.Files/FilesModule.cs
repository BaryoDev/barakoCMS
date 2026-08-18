using barakoCMS.Modules;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BarakoCMS.Files;

/// <summary>
/// Optional file-attachment module for barakoCMS. Enable it with:
/// <code>services.AddBarakoCMS(config, m =&gt; m.Add(new FilesModule()));</code>
/// Adds <c>POST /api/files</c> (upload), <c>GET /api/files/{id}</c> (authenticated download), and
/// <c>GET /api/public/files/{id}</c> (anonymous, public files only). Bytes go through
/// <see cref="IFileStorage"/> — Postgres by default, or an S3-compatible store when the
/// <c>BarakoCMS.Files.S3</c> module is also registered. Both work; the user chooses by whether they
/// add the S3 module and configure it.
/// </summary>
public sealed class FilesModule : IBarakoModule
{
    public string Name => "Files";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        /* Default storage. The S3 module, when present, replaces this (it runs after and overrides). */
        services.TryAddScoped<IFileStorage, PostgresFileStorage>();
    }

    public void ConfigureSchema(IModuleSchema schema)
    {
        schema.For<StoredFile>()
            .DocumentAlias("stored_files")
            .Index(x => x.CreatedAt)
            .Index(x => x.UploadedBy);

        /* Blob bytes for the Postgres provider, keyed by the storage key (a string id). */
        schema.For<FileBlob>()
            .DocumentAlias("file_blobs");
    }
}
