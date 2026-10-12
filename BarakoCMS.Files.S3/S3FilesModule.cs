using Amazon.Runtime;
using Amazon.S3;
using barakoCMS.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using BarakoCMS.Files.Storage;

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

        // Always, configured or not: a tenant can choose a bucket through the API, and whatever
        // configuration sets makes that choice read-only.
        services.AddSingleton<IObjectStorageProvider>(new S3ObjectStorageProvider(
            section.GetValue(S3ObjectStorageProvider.AllowPrivateServiceUrlSetting, false)));
        services.RemoveAll<IStorageEnvironment>();
        services.AddSingleton<IStorageEnvironment>(new ConfiguredStorageKeys(ConfiguredKeys(section)));

        /* Only take over when actually configured. This lets the Suite always include the module; with
         * no Files:S3:Bucket set it stays dormant and the Postgres default keeps serving. */
        if (string.IsNullOrWhiteSpace(section["Bucket"]))
            return;

        services.AddOptions<S3StorageOptions>().Bind(section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<S3StorageOptions>, S3StorageOptionsValidator>();

        services.AddSingleton<IAmazonS3>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<S3StorageOptions>>().Value;
            var bounds = o.Resolve(S3StorageOptionsValidator.JobLeaseSeconds(sp.GetService<IConfiguration>()));
            var cfg = new AmazonS3Config
            {
                ForcePathStyle = o.ForcePathStyle,
                MaxErrorRetry = bounds.MaxErrorRetry,
                Timeout = bounds.Timeout,
            };
            if (!string.IsNullOrEmpty(o.ServiceUrl))
                cfg.ServiceURL = o.ServiceUrl;                      /* R2 / self-hosted */
            else
                cfg.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(o.Region); /* AWS */
            return new AmazonS3Client(new BasicAWSCredentials(o.AccessKey, o.SecretKey), cfg);
        });

        /* Replace the Postgres default (runs after FilesModule's TryAdd). */
        services.RemoveAll<IFileStorage>();
        services.AddScoped<IFileStorage, S3FileStorage>();
    }

    /// <summary>
    /// The storage keys set in this module's section. The retry and timeout bounds and the private
    /// address switch tune the client rather than choose storage, so they are not among them.
    /// </summary>
    internal static IReadOnlyList<string> ConfiguredKeys(IConfiguration section)
    {
        string[] storageKeys =
        [
            nameof(S3StorageOptions.Bucket), nameof(S3StorageOptions.Region), nameof(S3StorageOptions.ServiceUrl),
            nameof(S3StorageOptions.PublicBaseUrl), nameof(S3StorageOptions.UsePublicReadAcl),
            nameof(S3StorageOptions.ForcePathStyle), nameof(S3StorageOptions.AccessKey), nameof(S3StorageOptions.SecretKey),
        ];
        return storageKeys.Where(k => !string.IsNullOrWhiteSpace(section[k])).ToList();
    }
}

/// <summary>
/// Checks the retry and timeout settings against the leases when the host starts, the way core checks
/// <c>Workflows:Outbound</c>. A validator rather than a check in ConfigureServices, because the job
/// lease is outside this module's own section.
/// </summary>
internal sealed class S3StorageOptionsValidator(IConfiguration? configuration = null) : IValidateOptions<S3StorageOptions>
{
    public ValidateOptionsResult Validate(string? name, S3StorageOptions options)
    {
        return options.Resolve(JobLeaseSeconds(configuration)).Problem is { } problem
            ? ValidateOptionsResult.Fail(problem)
            : ValidateOptionsResult.Success;
    }

    internal static int JobLeaseSeconds(IConfiguration? configuration) =>
        configuration?.GetValue(S3StorageOptions.JobLeaseSecondsKey, S3StorageOptions.DefaultJobLeaseSeconds)
        ?? S3StorageOptions.DefaultJobLeaseSeconds;
}
