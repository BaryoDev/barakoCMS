using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using Marten;

namespace barakoCMS.Features.Config;

/// <summary>What a plan or an apply answered, and the status it is answered with.</summary>
internal sealed record ConfigRunResult(int StatusCode, ConfigPlanResponse Response);

/// <summary>
/// Export, plan and apply over every registered <see cref="IConfigProvider"/>, in the caller's
/// tenant.
/// </summary>
/// <remarks>
/// <para>
/// A plan and an apply are the same run. Each section is planned, then applied inside one
/// transaction, so the next section is planned against what the ones before it leave: a workflow is
/// checked against the content type the same document creates. The transaction commits only for an
/// apply that nothing refused, and is rolled back for a plan, so a plan cannot write anything.
/// </para>
/// <para>
/// The tenant is the request's. Every provider reads and writes through the scoped session the
/// batch runner opens for that tenant, and nothing here takes a tenant as input.
/// </para>
/// </remarks>
internal sealed class ConfigEngine(
    IEnumerable<IConfigProvider> providers,
    ICallerCapabilities capabilities,
    IContentBatchRunner runner,
    IDocumentSession session,
    TenantContext tenant,
    IPermissionResolver permissions,
    barakoCMS.Infrastructure.OpenApi.DeliveryDocumentCache deliveryDocuments)
{
    public IReadOnlyList<string> Sections => Ordered(providers).Select(p => p.Section).ToList();

    public async Task<ConfigExportResponse> ExportAsync(
        ConfigCaller caller, IReadOnlyCollection<string>? only, CancellationToken ct)
    {
        var response = new ConfigExportResponse { ExportedAt = DateTime.UtcNow };

        foreach (var provider in Ordered(providers))
        {
            if (only is not null && !only.Contains(provider.Section))
            {
                continue;
            }

            if (!await HoldsAsync(caller, provider.ReadCapability, ct))
            {
                response.Withheld.Add(provider.Section);
                continue;
            }

            if (await provider.ExportAsync(caller, ct) is not { } export)
            {
                continue;
            }

            response.Sections[provider.Section] = ConfigJson.ToElement(ConfigJson.Sorted(export.Documents));
            if (export.Gaps.Count > 0)
            {
                response.Gaps[provider.Section] = export.Gaps.ToList();
            }
        }

        await AuditLog.RecordAsync(session, tenant.Slug, "config.exported", UserId(caller), Username(caller),
            metadata: new Dictionary<string, object>
            {
                ["sections"] = string.Join(", ", response.Sections.Keys),
                ["withheld"] = string.Join(", ", response.Withheld),
            }, ct: ct);
        await session.SaveChangesAsync(ct);

        return response;
    }

    /// <summary>
    /// Plans <paramref name="document"/>, and with <paramref name="apply"/> commits it when nothing is
    /// refused and every destructive change is allowed by <paramref name="allowDestructive"/>.
    /// </summary>
    public async Task<ConfigRunResult> RunAsync(
        JsonElement document, ConfigCaller caller, bool apply, bool allowDestructive, CancellationToken ct)
    {
        var parsed = ConfigDocument.Parse(document, Sections);
        if (parsed.Errors.Count > 0)
        {
            return new ConfigRunResult(400, new ConfigPlanResponse { Errors = parsed.Errors.ToList() });
        }

        var missing = await MissingCapabilitiesAsync(parsed, caller, apply, ct);
        if (missing.Count > 0)
        {
            return new ConfigRunResult(403, new ConfigPlanResponse { Refusals = missing });
        }

        var response = await runner.RunAsync(
            (scope, token) => RunInScopeAsync(scope, parsed, caller, apply, allowDestructive, token), ct);

        if (response.Applied)
        {
            // A role or a rule changed, or a deliverable type did: cached decisions and the
            // delivery document would otherwise answer from before the apply.
            permissions.InvalidateAllPermissions();
            deliveryDocuments.Invalidate(tenant.Slug);
        }

        return new ConfigRunResult(StatusFor(response, apply, allowDestructive), response);
    }

    private static int StatusFor(ConfigPlanResponse response, bool apply, bool allowDestructive)
    {
        if (!apply)
        {
            return 200;
        }

        if (response.HasRefusals)
        {
            return 403;
        }

        if (response.HasSectionErrors)
        {
            return 400;
        }

        return response.HasDestructiveChanges && !allowDestructive ? 409 : 200;
    }

    private async Task<ContentBatchOutcome<ConfigPlanResponse>> RunInScopeAsync(
        IServiceProvider scope, ParsedConfig parsed, ConfigCaller caller, bool apply, bool allowDestructive,
        CancellationToken ct)
    {
        var scoped = scope.GetRequiredService<IDocumentSession>();
        var plans = await PlanAndStageAsync(scope, scoped, parsed, caller, ct);

        var response = new ConfigPlanResponse { Sections = plans.Select(ConfigSectionPlanResponse.From).ToList() };
        var commit = apply
                     && !response.HasRefusals
                     && !response.HasSectionErrors
                     && (allowDestructive || !response.HasDestructiveChanges);

        if (commit && !response.IsEmpty)
        {
            var slug = scope.GetRequiredService<TenantContext>().Slug;
            await AuditLog.RecordAsync(scoped, slug, "config.applied", UserId(caller), Username(caller),
                metadata: AuditMetadata(response), ct: ct);
            await scoped.SaveChangesAsync(ct);
        }

        response.Applied = commit;
        return new ContentBatchOutcome<ConfigPlanResponse>(response, commit);
    }

    /// <summary>
    /// Each section planned and then staged, in order, until one is refused or fails. The ones after
    /// it are still planned, against what was staged before it, so one review shows every problem.
    /// </summary>
    private static async Task<List<ConfigSectionPlan>> PlanAndStageAsync(
        IServiceProvider scope, IDocumentSession scoped, ParsedConfig parsed, ConfigCaller caller, CancellationToken ct)
    {
        var plans = new List<ConfigSectionPlan>();
        var staging = true;

        foreach (var provider in Ordered(scope.GetServices<IConfigProvider>()).Where(p => parsed.Sections.ContainsKey(p.Section)))
        {
            var plan = await provider.PlanAsync(parsed.Sections[provider.Section], caller, ct);
            staging &= plan.Errors.Count == 0 && plan.Refusals.Count == 0;

            var aborted = false;
            if (staging && plan.Changes.Count > 0)
            {
                var failures = (await provider.ApplyAsync(plan, caller, ct)).ToList();
                if (failures.Count == 0 && await SaveAsync(scoped, ct) is { } conflict)
                {
                    // The database refused the save, which aborts the transaction, so nothing after
                    // this can be read in it.
                    failures.Add(conflict);
                    aborted = true;
                }

                if (failures.Count > 0)
                {
                    staging = false;
                    plan = new ConfigSectionPlan
                    {
                        Section = plan.Section,
                        Changes = plan.Changes,
                        Errors = [.. plan.Errors, .. failures],
                        Refusals = plan.Refusals,
                        Notes = plan.Notes,
                    };
                }
            }

            plans.Add(plan);
            if (aborted)
            {
                break;
            }
        }

        return plans;
    }

    /// <summary>Saves what a section staged, or says why the database refused it.</summary>
    private static async Task<string?> SaveAsync(IDocumentSession scoped, CancellationToken ct)
    {
        try
        {
            await scoped.SaveChangesAsync(ct);
            return null;
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            return "Another write took a name this section uses at the same time. Plan again.";
        }
    }

    private async Task<List<string>> MissingCapabilitiesAsync(
        ParsedConfig parsed, ConfigCaller caller, bool apply, CancellationToken ct)
    {
        var missing = new List<string>();

        foreach (var provider in Ordered(providers).Where(p => parsed.Sections.ContainsKey(p.Section)))
        {
            var needed = apply ? new[] { provider.ReadCapability, provider.WriteCapability } : [provider.ReadCapability];
            foreach (var capability in needed.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!await HoldsAsync(caller, capability, ct))
                {
                    missing.Add($"The '{provider.Section}' section needs the '{capability}' capability, which this caller is missing.");
                }
            }
        }

        return missing;
    }

    private async Task<bool> HoldsAsync(ConfigCaller caller, string capability, CancellationToken ct) =>
        caller.Principal is not { } principal || await capabilities.HoldsAsync(principal, capability, ct);

    private static IEnumerable<IConfigProvider> Ordered(IEnumerable<IConfigProvider> all) =>
        all.OrderBy(p => p.Order).ThenBy(p => p.Section, StringComparer.Ordinal);

    private static Dictionary<string, object> AuditMetadata(ConfigPlanResponse response)
    {
        var changes = response.Sections.SelectMany(s => s.Changes).ToList();
        return new Dictionary<string, object>
        {
            ["sections"] = string.Join(", ", response.Sections.Where(s => s.Changes.Count > 0).Select(s => s.Section)),
            ["created"] = changes.Count(c => c.Kind == "create"),
            ["changed"] = changes.Count(c => c.Kind == "change"),
            ["destructive"] = changes.Count(c => c.Kind == ConfigChangeResponse.DestructiveKind),
        };
    }

    private static Guid? UserId(ConfigCaller caller) =>
        Guid.TryParse(caller.Principal?.FindFirst("UserId")?.Value, out var id) ? id : null;

    private static string? Username(ConfigCaller caller) =>
        caller.IsSystem ? "system" : caller.Principal!.FindFirst("Username")?.Value;

    private static bool IsUniqueViolation(Exception? ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is Npgsql.PostgresException { SqlState: "23505" })
            {
                return true;
            }
        }

        return false;
    }
}
