using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using BarakoCMS.Files.Storage;
using FastEndpoints;
using FluentValidation;
using Marten;

namespace BarakoCMS.Files.Features.StorageSettings;

/// <summary>A bucket as a caller writes it. The secret is write-only.</summary>
internal sealed class S3SettingsRequest
{
    public string Bucket { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-1";
    public string? ServiceUrl { get; set; }
    public string? PublicBaseUrl { get; set; }
    public bool UsePublicReadAcl { get; set; }
    public bool ForcePathStyle { get; set; } = true;
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>Null keeps the stored secret, empty clears it, anything else replaces it.</summary>
    /// <remarks>
    /// Null and empty differ because the screen cannot show the stored value, so it has no way to
    /// send it back unchanged. Treating an absent field as "clear it" would wipe the secret every
    /// time somebody corrected the region.
    /// </remarks>
    public string? SecretKey { get; set; }
}

internal sealed class StorageSettingsRequest
{
    /// <summary><c>postgres</c> or <c>s3</c>: where new uploads go.</summary>
    public string Provider { get; set; } = StorageProviders.Postgres;

    /// <summary>The bucket. Required for <c>s3</c>; kept when switching back to <c>postgres</c> if left out.</summary>
    public S3SettingsRequest? S3 { get; set; }
}

internal sealed class StorageSettingsValidator : Validator<StorageSettingsRequest>
{
    public StorageSettingsValidator()
    {
        RuleFor(x => x.Provider)
            .Must(p => p is StorageProviders.Postgres or StorageProviders.S3)
            .WithMessage("provider must be 'postgres' or 's3'.");
        RuleFor(x => x.S3)
            .NotNull()
            .When(x => x.Provider == StorageProviders.S3)
            .WithMessage("s3 is required when provider is 's3'.");
        RuleFor(x => x.S3!.Bucket).NotEmpty().MaximumLength(255).When(x => x.S3 is not null);
        RuleFor(x => x.S3!.Region).NotEmpty().MaximumLength(64).Matches("^[a-z0-9-]+$")
            .When(x => x.S3 is not null)
            .WithMessage("s3.region must be a region name such as us-east-1.");
        RuleFor(x => x.S3!.AccessKey).NotEmpty().MaximumLength(256).When(x => x.S3 is not null);
        RuleFor(x => x.S3!.SecretKey).MaximumLength(1024).When(x => x.S3 is not null);
        RuleFor(x => x.S3!.ServiceUrl)
            .Must(BeHttpUrl).When(x => !string.IsNullOrEmpty(x.S3?.ServiceUrl))
            .WithMessage("s3.serviceUrl must be an absolute http or https URL.");
        RuleFor(x => x.S3!.PublicBaseUrl)
            .Must(BeHttpUrl).When(x => !string.IsNullOrEmpty(x.S3?.PublicBaseUrl))
            .WithMessage("s3.publicBaseUrl must be an absolute http or https URL.");
    }

    private static bool BeHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}

/// <summary>A bucket as the API describes it: whether a secret is set, never the secret.</summary>
internal sealed class S3SettingsResponse
{
    public string Bucket { get; init; } = string.Empty;
    public string Region { get; init; } = string.Empty;
    public string? ServiceUrl { get; init; }
    public string? PublicBaseUrl { get; init; }
    public bool UsePublicReadAcl { get; init; }
    public bool ForcePathStyle { get; init; }
    public string AccessKey { get; init; } = string.Empty;
    public bool SecretSet { get; init; }

    /// <summary>
    /// A secret is stored but cannot be decrypted, which happens when the deployment's key changes.
    /// New uploads then go to Postgres until the secret is sent again.
    /// </summary>
    public bool SecretUnreadable { get; init; }
}

internal sealed class StorageSettingsResponse
{
    /// <summary>What the tenant saved: <c>postgres</c> or <c>s3</c>.</summary>
    public string Provider { get; init; } = StorageProviders.Postgres;

    public S3SettingsResponse? S3 { get; init; }

    /// <summary>Where new uploads go right now, which differs from <see cref="Provider"/> when configuration decides.</summary>
    public string ActiveProvider { get; init; } = StorageProviders.Postgres;

    /// <summary>Whether configuration sets storage, so <c>PUT</c> is refused.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>The <c>Files:S3</c> keys set in configuration, such as <c>Bucket</c>.</summary>
    public IReadOnlyList<string> EnvironmentKeys { get; init; } = [];

    /// <summary>Which providers this deployment can build from a saved setting.</summary>
    public IReadOnlyList<string> AvailableProviders { get; init; } = [];

    public DateTime? UpdatedAt { get; init; }
    public string? UpdatedBy { get; init; }

    internal static async Task<StorageSettingsResponse> FromAsync(
        Storage.StorageSettings? stored, FileStorageResolver storages, IStorageEnvironment environment,
        IEnumerable<IObjectStorageProvider> providers, ISecretProtector protector, CancellationToken ct) => new()
    {
        Provider = stored?.Provider ?? StorageProviders.Postgres,
        S3 = stored?.S3 is { } s3
            ? new S3SettingsResponse
            {
                Bucket = s3.Bucket,
                Region = s3.Region,
                ServiceUrl = s3.ServiceUrl,
                PublicBaseUrl = s3.PublicBaseUrl,
                UsePublicReadAcl = s3.UsePublicReadAcl,
                ForcePathStyle = s3.ForcePathStyle,
                AccessKey = s3.AccessKey,
                SecretSet = !string.IsNullOrEmpty(s3.ProtectedSecretKey),
                SecretUnreadable = !string.IsNullOrEmpty(s3.ProtectedSecretKey)
                    && string.IsNullOrEmpty(protector.Unprotect(s3.ProtectedSecretKey)),
            }
            : null,
        ActiveProvider = (await storages.ForNewFilesAsync(ct)).Provider,
        ReadOnly = storages.ConfigurationDecides,
        EnvironmentKeys = environment.Keys,
        AvailableProviders = [StorageProviders.Postgres, .. providers.Select(p => p.Provider)],
        UpdatedAt = stored?.UpdatedAt,
        UpdatedBy = stored?.UpdatedBy,
    };
}

internal static class StorageSettingsGate
{
    public static void Apply(EndpointDefinition definition) =>
        definition.RequireCapability(SystemCapabilities.ManageStorage, "SuperAdmin", "Admin");
}

/// <summary>GET /api/settings/storage: where this tenant's files go, and whether it can be changed here.</summary>
internal sealed class GetStorageSettingsEndpoint(
    IQuerySession session,
    FileStorageResolver storages,
    IStorageEnvironment environment,
    IEnumerable<IObjectStorageProvider> providers,
    ISecretProtector protector) : EndpointWithoutRequest<StorageSettingsResponse>
{
    public override void Configure()
    {
        Get("/api/settings/storage");
        StorageSettingsGate.Apply(Definition);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var stored = await session.LoadAsync<Storage.StorageSettings>(Storage.StorageSettings.SingletonId, ct);
        await Send.OkAsync(await StorageSettingsResponse.FromAsync(stored, storages, environment, providers, protector, ct), ct);
    }
}

/// <summary>
/// PUT /api/settings/storage: where this tenant's new uploads go, from the next request on.
/// </summary>
/// <remarks>
/// Moves no file. Files already stored stay where they are and keep downloading, because each read
/// follows the provider its file records. Refused with a 409 naming the keys while configuration
/// sets storage, since configuration would win anyway and a setting that does nothing should not
/// look saved.
/// </remarks>
internal sealed class UpdateStorageSettingsEndpoint(
    IDocumentSession session,
    FileStorageResolver storages,
    IStorageEnvironment environment,
    IEnumerable<IObjectStorageProvider> providers,
    ISecretProtector protector,
    TenantContext tenant) : Endpoint<StorageSettingsRequest, StorageSettingsResponse>
{
    public override void Configure()
    {
        Put("/api/settings/storage");
        StorageSettingsGate.Apply(Definition);
    }

    public override async Task HandleAsync(StorageSettingsRequest req, CancellationToken ct)
    {
        if (storages.ConfigurationDecides)
        {
            ThrowError(ConfiguredMessage(environment), 409);
        }

        if (req.Provider != StorageProviders.Postgres && storages.Provider(req.Provider) is null)
        {
            ThrowError($"No storage module for '{req.Provider}' is installed. Add BarakoCMS.Files.S3 and restart.", 400);
        }

        var settings = await session.LoadAsync<Storage.StorageSettings>(Storage.StorageSettings.SingletonId, ct)
            ?? new Storage.StorageSettings();

        var changed = new List<string>();
        if (settings.Provider != req.Provider)
        {
            changed.Add("provider");
        }

        settings.Provider = req.Provider;

        var hadBucket = settings.S3 is not null;
        if (req.S3 is { } s3)
        {
            var next = StorageSettingsMapping.Apply(settings.S3, s3, protector, changed);
            settings.S3 = next;
        }

        if (settings.Provider == StorageProviders.S3 && string.IsNullOrEmpty(settings.S3?.ProtectedSecretKey))
        {
            ThrowError("s3.secretKey is required: none is stored for this tenant.", 400);
        }

        // Files in the bucket are read through these settings, since a file records only that it
        // is in a bucket, not which one. Pointing the settings elsewhere, or clearing the secret,
        // would leave every one of them unreadable, so it is refused while any is there.
        var moved = changed.Where(StorageSettingsMapping.MovesTheBucket).ToList();
        if (hadBucket && moved.Count > 0
            && await session.Query<StoredFile>().AnyAsync(f => f.Provider == StorageProviders.S3, ct))
        {
            ThrowError(
                $"Files are stored in this tenant's bucket, and changing {string.Join(", ", moved)} would leave them "
              + "unreadable. Keep the bucket's settings, or move the files first. The provider can still be switched, "
              + "and the secret replaced with a new value.", 409);
        }

        if (changed.Count > 0)
        {
            settings.UpdatedAt = DateTime.UtcNow;
            settings.UpdatedBy = User.FindFirst("Username")?.Value ?? User.Identity?.Name;
            session.Store(settings);

            // Which fields changed, never their values: the audit log is never deleted from, and
            // a secret quoted there would outlive every rotation.
            Guid? actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var id) ? id : null;
            await AuditLog.RecordAsync(session, tenant.Slug, "settings.storage.changed",
                actorId, settings.UpdatedBy,
                targetType: nameof(Storage.StorageSettings), targetId: Storage.StorageSettings.SingletonId,
                metadata: new() { ["fields"] = string.Join(", ", changed), ["provider"] = settings.Provider },
                ct: ct);

            await session.SaveChangesAsync(ct);
            storages.Forget();
        }

        await Send.OkAsync(await StorageSettingsResponse.FromAsync(settings, storages, environment, providers, protector, ct), ct);
    }

    internal static string ConfiguredMessage(IStorageEnvironment environment) =>
        environment.Keys.Count > 0
            ? $"Storage is set in configuration ({string.Join(", ", environment.Keys)}, in the Files.S3 module's section: "
              + "Modules:Files.S3 or the older Files:S3), which applies to every tenant, so it cannot be changed here. "
              + "Remove those settings and restart to choose it here."
            : "Storage is set in configuration, which applies to every tenant, so it cannot be changed here.";
}

internal sealed class StorageTestResponse
{
    public bool Ok { get; init; }

    /// <summary>The step that failed: <c>put</c>, <c>get</c> or <c>delete</c>. Null when it worked.</summary>
    public string? Step { get; init; }

    /// <summary>The provider's error code, such as <c>InvalidAccessKeyId</c> or <c>NoSuchBucket</c>.</summary>
    public string? ErrorCode { get; init; }

    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// POST /api/settings/storage/test: puts, reads and deletes a small object with the settings sent,
/// without saving them, and says which step failed.
/// </summary>
/// <remarks>
/// A secret left out is taken from the stored setting, so a screen can test a corrected region
/// without asking for the secret again. Nothing is saved whatever the outcome, and the secret is in
/// no answer, including the provider's error message, which never quotes it.
/// </remarks>
internal sealed class TestStorageSettingsEndpoint(
    IQuerySession session,
    FileStorageResolver storages,
    ISecretProtector protector) : Endpoint<StorageSettingsRequest, StorageTestResponse>
{
    public override void Configure()
    {
        Post("/api/settings/storage/test");
        StorageSettingsGate.Apply(Definition);
    }

    public override async Task HandleAsync(StorageSettingsRequest req, CancellationToken ct)
    {
        if (req.Provider == StorageProviders.Postgres)
        {
            await Send.OkAsync(new StorageTestResponse { Ok = true, Message = "Postgres needs no test: it is the database the API already uses." }, ct);
            return;
        }

        if (storages.Provider(req.Provider) is not { } provider)
        {
            ThrowError($"No storage module for '{req.Provider}' is installed. Add BarakoCMS.Files.S3 and restart.", 400);
            return;
        }

        var stored = await session.LoadAsync<Storage.StorageSettings>(Storage.StorageSettings.SingletonId, ct);
        var settings = StorageSettingsMapping.Apply(stored?.S3, req.S3!, protector, []);
        if (string.IsNullOrEmpty(settings.ProtectedSecretKey))
        {
            ThrowError("s3.secretKey is required: none was sent and none is stored for this tenant.", 400);
            return;
        }

        var secret = protector.Unprotect(settings.ProtectedSecretKey);
        if (string.IsNullOrEmpty(secret))
        {
            ThrowError("The stored secret could not be decrypted, which happens when the deployment's key changes. Send s3.secretKey again.", 400);
            return;
        }

        var result = await provider.TestAsync(settings, secret, ct);
        await Send.OkAsync(new StorageTestResponse
        {
            Ok = result.Ok,
            Step = result.Step,
            ErrorCode = result.ErrorCode,
            Message = result.Message,
        }, ct);
    }
}

internal static class StorageSettingsMapping
{
    /// <summary>The stored bucket with the request applied, noting each field that changed.</summary>
    public static ObjectStorageSettings Apply(
        ObjectStorageSettings? stored, S3SettingsRequest req, ISecretProtector protector, List<string> changed)
    {
        var next = stored?.Copy() ?? new ObjectStorageSettings();

        void Set<T>(string name, T current, T value, Action<T> assign)
        {
            if (!EqualityComparer<T>.Default.Equals(current, value))
            {
                assign(value);
                changed.Add($"s3.{name}");
            }
        }

        Set("bucket", next.Bucket, req.Bucket.Trim(), v => next.Bucket = v);
        Set("region", next.Region, req.Region.Trim(), v => next.Region = v);
        Set("serviceUrl", next.ServiceUrl, Blank(req.ServiceUrl), v => next.ServiceUrl = v);
        Set("publicBaseUrl", next.PublicBaseUrl, Blank(req.PublicBaseUrl), v => next.PublicBaseUrl = v);
        Set("usePublicReadAcl", next.UsePublicReadAcl, req.UsePublicReadAcl, v => next.UsePublicReadAcl = v);
        Set("forcePathStyle", next.ForcePathStyle, req.ForcePathStyle, v => next.ForcePathStyle = v);
        Set("accessKey", next.AccessKey, req.AccessKey.Trim(), v => next.AccessKey = v);

        if (req.SecretKey is not null)
        {
            var trimmed = req.SecretKey.Trim();
            next.ProtectedSecretKey = trimmed.Length == 0 ? null : protector.Protect(trimmed);
            changed.Add(trimmed.Length == 0 ? "s3.secretKey.cleared" : "s3.secretKey.set");
        }

        return next;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Whether a changed field points the settings at another bucket, or takes away the way in.</summary>
    public static bool MovesTheBucket(string field) =>
        field is "s3.bucket" or "s3.region" or "s3.serviceUrl" or "s3.secretKey.cleared";
}
