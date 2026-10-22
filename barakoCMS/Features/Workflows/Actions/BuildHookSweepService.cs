using barakoCMS.Infrastructure.Http;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Hosting;

namespace barakoCMS.Features.Workflows.Actions;

/// <summary>
/// Fires the build hooks whose quiet period has ended. A due row is claimed with a versioned write
/// before anything is sent, so when several instances sweep together only one of them fires it.
/// </summary>
/// <remarks>
/// The row is removed after the post whether or not it succeeded. A refused or failing hook leaves a
/// <see cref="WebhookDelivery"/> row to say so, and the next publish starts a new period. Retrying
/// here would turn a provider outage into a stream of builds the moment it recovers.
///
/// A claim left behind by an instance that died is taken over after <see cref="ClaimTimeout"/>.
/// </remarks>
internal sealed class BuildHookSweepService(
    IDocumentStore store,
    IConfiguration config,
    IHttpClientFactory httpClientFactory,
    ISecretProtector protector,
    OutboundAddressGuard addressGuard,
    ILoggerFactory loggers,
    TimeProvider clock) : BackgroundService
{
    public static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);

    private const string PartitionsWithPendingBuildsSql =
        "select distinct tenant_id from public.mt_doc_pending_builds";

    private readonly ILogger _logger = loggers.CreateLogger<BuildHookSweepService>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var fired = await SweepAllTenantsAsync(stoppingToken);
                if (fired > 0) _logger.LogInformation("Build hook sweep fired {Count} build(s)", fired);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during the build hook sweep");
            }

            try
            {
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task<int> SweepAllTenantsAsync(CancellationToken ct)
    {
        var fired = 0;

        foreach (var tenantId in await TenantPartitions.ListAsync(store, config, PartitionsWithPendingBuildsSql, ct))
        {
            await using var session = store.LightweightSession(tenantId);
            fired += await SweepTenantAsync(session, ct);
        }

        return fired;
    }

    /// <summary>Fires every due build in one partition. A test drives it without the timer.</summary>
    /// <returns>How many builds this call fired.</returns>
    public async Task<int> SweepTenantAsync(IDocumentSession session, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var reclaimBefore = now - ClaimTimeout;

        var due = await session.Query<PendingBuild>()
            .Where(p => p.FireAt <= now && (p.ClaimedAt == null || p.ClaimedAt < reclaimBefore))
            .OrderBy(p => p.FireAt)
            .Take(100)
            .ToListAsync(ct);

        var fired = 0;

        foreach (var pending in due)
        {
            if (!await TryClaimAsync(session, pending, now, ct)) continue;

            await FireAsync(session, pending, ct);

            session.Delete<PendingBuild>(pending.Id);
            await session.SaveChangesAsync(ct);
            fired++;
        }

        return fired;
    }

    private static async Task<bool> TryClaimAsync(IDocumentSession session, PendingBuild pending, DateTimeOffset now, CancellationToken ct)
    {
        pending.ClaimedAt = now;
        session.Store(pending);

        try
        {
            await session.SaveChangesAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is JasperFx.ConcurrencyException || ex.GetType().Name.Contains("Concurrency"))
        {
            return false;
        }
    }

    private async Task FireAsync(IDocumentSession session, PendingBuild pending, CancellationToken ct)
    {
        var parameters = new Dictionary<string, string>(pending.Parameters);
        var url = BuildHookPresets.UrlFor(parameters)!;
        var tenant = TenantScopes.SlugFor(session.TenantId);

        string? bearer = null;
        if (BuildHookPresets.PresetOf(parameters) == BuildHookPresets.GithubDispatch)
        {
            bearer = protector.Unprotect(parameters.GetValueOrDefault(WebhookSigning.SecretParameter) ?? string.Empty);
            if (bearer is null)
            {
                await RecordRefusedAsync(session, pending, url, "The build hook token could not be decrypted. Enter it again on the workflow.", ct);
                return;
            }
        }

        // The secret is a bearer token here, not a signing key, so the webhook must not see it.
        parameters.Remove(WebhookSigning.SecretParameter);
        parameters["Url"] = url;
        parameters["IdempotencyKey"] = $"build-{pending.Id}-{pending.FireAt.ToUnixTimeSeconds()}";

        var hook = new WebhookAction(
            httpClientFactory, session, protector, addressGuard, loggers.CreateLogger<WebhookAction>(), config);

        var stand = new barakoCMS.Models.Content { Id = Guid.Empty, ContentType = "build" };
        var result = await hook.RunAsync(parameters, stand, BuildHookPresets.Compose(parameters, tenant, bearer), ct);

        if (!result.Succeeded)
        {
            _logger.LogWarning("Build hook {Id} did not go through: {Reason}", pending.Id, result.Error);
        }
    }

    private static async Task RecordRefusedAsync(IDocumentSession session, PendingBuild pending, string url, string error, CancellationToken ct)
    {
        session.Store(new WebhookDelivery
        {
            Id = Guid.NewGuid(),
            Url = WebhookAction.Redact(url),
            Event = pending.Parameters.GetValueOrDefault("TriggerEvent") ?? string.Empty,
            WorkflowId = Guid.TryParse(pending.Parameters.GetValueOrDefault("WorkflowId"), out var id) ? id : Guid.Empty,
            Error = error,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await session.SaveChangesAsync(ct);
    }
}
