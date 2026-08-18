using Amazon.Runtime;
using Amazon.S3;
using barakoCMS.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Files.S3;

/// <summary>
/// Opt-in S3-compatible storage for the Files module. Register it AFTER FilesModule:
/// <code>m.Add(new FilesModule()); m.Add(new S3FilesModule());</code>
/// It replaces the default Postgres storage with <see cref="S3FileStorage"/>, so uploads land in the
/// configured bucket and public files get direct URLs. Configure under <c>Files:S3</c>.
/// </summary>
public sealed class S3FilesModule : IBarakoModule
{
    public string Name => "Files.S3";

    /// <summary>
    /// Files first. This module replaces the storage FilesModule registers, and RemoveAll only
    /// removes what is already there.
    /// </summary>
    /// <remarks>
    /// Benign today by accident: FilesModule uses TryAddScoped, so S3 wins whichever order they run
    /// in. It stops being benign the moment that becomes a plain AddScoped, and nothing would say
    /// so. Declared rather than left to the order someone happened to write in Program.cs.
    /// </remarks>
    public IEnumerable<string> DependsOn => ["Files"];

    /// <summary>Settings used to live at the root "Files:S3" section. See IBarakoModule.</summary>
    public string? LegacyConfigurationSection => "Files:S3";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // `configuration` is already this module's own section (Modules:Files.S3).
        var section = configuration;

        /* Only take over when actually configured. This lets the Suite always include the module; with
         * no Files:S3:Bucket set it stays dormant and the Postgres default keeps serving. */
        if (string.IsNullOrWhiteSpace(section["Bucket"]))
            return;

        services.Configure<S3StorageOptions>(section);

        services.AddSingleton<IAmazonS3>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<S3StorageOptions>>().Value;
            var cfg = new AmazonS3Config { ForcePathStyle = o.ForcePathStyle };
            if (!string.IsNullOrEmpty(o.ServiceUrl))
                cfg.ServiceURL = o.ServiceUrl;                      /* R2 / MinIO */
            else
                cfg.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(o.Region); /* AWS */
            return new AmazonS3Client(new BasicAWSCredentials(o.AccessKey, o.SecretKey), cfg);
        });

        /* Replace the Postgres default (runs after FilesModule's TryAdd). */
        services.RemoveAll<IFileStorage>();
        services.AddScoped<IFileStorage, S3FileStorage>();
    }
}
