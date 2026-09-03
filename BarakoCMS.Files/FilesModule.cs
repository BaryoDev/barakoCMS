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

        // Scanning is off unless Files:Scanner:Address names a clamd. That is the default and it is
        // what every existing deployment does, so upgrading changes nothing about what an upload
        // does. Registered as a concrete type either way so the upload path has one object to talk
        // to; which one it gets is the only difference.
        var scanner = configuration[$"{FileScannerOptions.Section}:Address"];

        if (string.IsNullOrWhiteSpace(scanner))
        {
            services.TryAddSingleton<IFileScanner, NoFileScanner>();
        }
        else
        {
            services.TryAddSingleton<IFileScanner, ClamAvScanner>();
        }
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

    /// <summary>
    /// Gives this module's capabilities to the roles that already reached its endpoints.
    /// </summary>
    /// <remarks>
    /// Core cannot do this: <c>SystemCapabilities.DefaultsFor</c> does not know this module exists.
    /// Without it the endpoints would be reachable only through the legacy role-name fallback, and
    /// turning that off, which is the point of issue #443, would take the module away from every
    /// Admin. Additive and idempotent, and it skips a role the host never seeded.
    /// </remarks>
    public Task SeedAsync(IDocumentSession session, IServiceProvider services, CancellationToken ct) =>
        ModuleCapabilities.GrantAsync(session, FileCapabilities.SeededRoles, FileCapabilities.All, ct);
}
