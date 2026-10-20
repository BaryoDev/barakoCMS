using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Attributes;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.Workflows.Actions;

/// <summary>
/// Fires a static build once per burst of publishes. The first publish in a quiet period leaves a
/// <see cref="PendingBuild"/> behind; every later publish in the period finds it and does nothing.
/// <see cref="BuildHookSweepService"/> posts the hook when the period ends.
/// </summary>
/// <remarks>
/// A build hook has no idempotency, so every fire costs a full build on the provider's plan. The post
/// itself goes through <see cref="WebhookAction"/>, so a refused address, a 500 or a timeout leaves a
/// <see cref="WebhookDelivery"/> row like any other webhook. See <c>docs/build-triggers.md</c>.
/// </remarks>
[WorkflowActionMetadata(
    Description = "Fire a static build hook (Netlify, Vercel, Cloudflare Pages, GitHub dispatch) once per burst of publishes",
    Group = WorkflowActionGroup.Delivery,
    RequiredParameters = new[] { "Preset" },
    OptionalParameters = new[] { "Url", "Secret", "Repository", "EventType", "QuietSeconds" },
    ExampleJson = @"{""Type"":""BuildHook"",""Parameters"":{""Preset"":""netlify"",""Url"":""https://api.netlify.com/build_hooks/abc123"",""QuietSeconds"":""60""}}"
)]
internal sealed class BuildHookAction : IWorkflowAction
{
    public const string TypeName = "BuildHook";
    public const int DefaultQuietSeconds = 60;
    private const int MaxQuietSeconds = 86_400;

    private readonly IDocumentSession _session;
    private readonly TimeProvider _clock;
    private readonly ILogger<BuildHookAction> _logger;

    public BuildHookAction(IDocumentSession session, ILogger<BuildHookAction> logger, TimeProvider? clock = null)
    {
        _session = session;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string Type => TypeName;

    /// <inheritdoc />
    public async Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
        => await RunAsync(parameters, content, ct);

    /// <inheritdoc />
    public async Task<WorkflowActionResult> RunAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
    {
        var problem = BuildHookPresets.Problem(parameters);
        if (problem is not null)
        {
            _logger.LogWarning("Build hook not scheduled: {Problem}", problem);
            return WorkflowActionResult.PermanentFailure(problem);
        }

        var key = BuildHookPresets.ActionKey(parameters);

        if (await _session.LoadAsync<PendingBuild>(key, ct) is not null)
        {
            return Outcome("debounced");
        }

        _session.Insert(new PendingBuild
        {
            Id = key,
            ActionKey = key,
            TenantId = TenantScopes.SlugFor(_session.TenantId),
            FireAt = _clock.GetUtcNow().AddSeconds(QuietSeconds(parameters)),
            Parameters = BuildHookPresets.Kept(parameters),
        });

        try
        {
            await _session.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex.GetType().Name == "DocumentAlreadyExistsException")
        {
            // Another publish in the same instant wrote the row first. That one is the burst's.
            return Outcome("debounced");
        }

        return Outcome("scheduled");
    }

    /// <summary>The quiet period in seconds, clamped to something a person meant.</summary>
    internal static int QuietSeconds(IReadOnlyDictionary<string, string> parameters) =>
        int.TryParse(parameters.GetValueOrDefault("QuietSeconds"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? Math.Clamp(seconds, 1, MaxQuietSeconds)
            : DefaultQuietSeconds;

    private static WorkflowActionResult Outcome(string build) =>
        WorkflowActionResult.Success(new Dictionary<string, string> { ["build"] = build });
}

/// <summary>
/// The four presets: what each provider expects on the wire, and the checks that run when a publish
/// arrives, so a misconfigured action fails the run instead of failing silently minutes later.
/// </summary>
internal static class BuildHookPresets
{
    public const string Netlify = "netlify";
    public const string Vercel = "vercel";
    public const string CloudflarePages = "cloudflare-pages";
    public const string GithubDispatch = "github-dispatch";

    public const string DefaultEventType = "barako-build";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Parameters the runner adds per attempt. A pending build is not tied to the run that wrote it.</summary>
    private static readonly string[] PerRun = ["RunId", "Attempt", "IdempotencyKey"];

    public static string? PresetOf(IReadOnlyDictionary<string, string> parameters) =>
        parameters.GetValueOrDefault("Preset")?.Trim().ToLowerInvariant() switch
        {
            Netlify => Netlify,
            Vercel => Vercel,
            CloudflarePages => CloudflarePages,
            GithubDispatch => GithubDispatch,
            _ => null,
        };

    /// <summary>The reason this configuration cannot work, or null.</summary>
    public static string? Problem(IReadOnlyDictionary<string, string> parameters)
    {
        var preset = PresetOf(parameters);
        if (preset is null)
        {
            return "The BuildHook Preset must be one of netlify, vercel, cloudflare-pages or github-dispatch.";
        }

        if (UrlFor(parameters) is null)
        {
            return preset == GithubDispatch
                ? "The github-dispatch preset needs a Repository (owner/name) or a Url."
                : $"The {preset} preset needs the build hook Url.";
        }

        if (preset == GithubDispatch)
        {
            if (!WebhookSigning.HasSecret(parameters))
            {
                return "The github-dispatch preset needs a Secret holding a token that may dispatch to the repository.";
            }

            // The token goes out as a bearer header, so it is never sent over plain http.
            if (!UrlFor(parameters)!.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return "The github-dispatch preset sends a token and needs an https Url.";
            }
        }

        return null;
    }

    public static string? UrlFor(IReadOnlyDictionary<string, string> parameters)
    {
        var url = parameters.GetValueOrDefault("Url");
        if (!string.IsNullOrWhiteSpace(url)) return url.Trim();

        var repository = parameters.GetValueOrDefault("Repository")?.Trim();
        return PresetOf(parameters) == GithubDispatch && !string.IsNullOrEmpty(repository)
            ? $"https://api.github.com/repos/{repository}/dispatches"
            : null;
    }

    /// <summary>One key per workflow and target, so two workflows can each build the same site.</summary>
    public static string ActionKey(IReadOnlyDictionary<string, string> parameters)
    {
        var material = string.Join('|',
            parameters.GetValueOrDefault("WorkflowId") ?? string.Empty,
            PresetOf(parameters),
            UrlFor(parameters));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..32];
    }

    public static Dictionary<string, string> Kept(IReadOnlyDictionary<string, string> parameters) =>
        parameters.Where(p => !PerRun.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);

    /// <summary>The body and headers one provider expects.</summary>
    public static OutboundOverride Compose(IReadOnlyDictionary<string, string> parameters, string tenant, string? bearer)
    {
        var preset = PresetOf(parameters);
        var headers = new Dictionary<string, string>();

        object body = preset switch
        {
            Netlify => new { trigger_title = "barakoCMS publish" },
            GithubDispatch => new
            {
                event_type = parameters.GetValueOrDefault("EventType") is { Length: > 0 } type ? type : DefaultEventType,
                client_payload = new { tenant, workflowId = parameters.GetValueOrDefault("WorkflowId") },
            },
            _ => new { },
        };

        if (preset == GithubDispatch && bearer is not null)
        {
            headers["Authorization"] = "Bearer " + bearer;
            headers["Accept"] = "application/vnd.github+json";
            headers["X-GitHub-Api-Version"] = "2022-11-28";
            headers["User-Agent"] = "barakoCMS";
        }

        return new OutboundOverride(JsonSerializer.SerializeToUtf8Bytes(body, Json), headers);
    }
}
