using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using BarakoCMS.Diagnostics.Redaction;
using FastEndpoints;
using FluentValidation;
using Marten;

namespace BarakoCMS.Diagnostics.Features.Incidents;

internal sealed class IncidentsRequest : PaginatedRequest
{
    /// <summary>Only this source, for example <c>client-errors</c>. Null reads every source the caller may read.</summary>
    public string? Source { get; set; }

    /// <summary>Filter by resolved state. Null means both.</summary>
    public bool? Resolved { get; set; }

    /// <summary>How far back to look, in days. 30 when unset.</summary>
    public int? Days { get; set; }
}

internal sealed class IncidentsValidator : Validator<IncidentsRequest>
{
    public const int MaxDays = 90;

    public IncidentsValidator()
    {
        RuleFor(x => x.Days).InclusiveBetween(1, MaxDays).When(x => x.Days is not null);
        RuleFor(x => x.Source).MaximumLength(100);
    }
}

/// <summary>One fault, grouped across every occurrence the sources returned for it.</summary>
internal sealed class IncidentGroup
{
    public string Source { get; init; } = string.Empty;
    public string Fingerprint { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Detail { get; init; }
    public int Count { get; init; }
    public DateTimeOffset FirstSeenAt { get; init; }
    public DateTimeOffset LastSeenAt { get; init; }
    public List<string> Tenants { get; init; } = [];
    public List<string> Releases { get; init; } = [];
    public bool Resolved { get; init; }
    public DateTimeOffset? ResolvedAt { get; init; }
    public string? ResolvedBy { get; init; }
    public string? ResolutionReference { get; init; }
    public string? ResolutionNote { get; init; }
}

/// <summary>
/// GET /api/incidents: one paged list across client errors, dead-lettered jobs, failed workflow
/// actions and failed deliveries, grouped by fingerprint and redacted here, so no client has to
/// regroup or re-redact and nothing personal leaves the API on the way (#893).
/// </summary>
internal sealed class Endpoint(
    IEnumerable<IIncidentSource> sources,
    IQuerySession session,
    TenantContext tenant,
    ICallerCapabilities capabilities,
    IConfiguration configuration) : Endpoint<IncidentsRequest, PaginatedResponse<IncidentGroup>>
{
    /// <summary>The most occurrences read from any one source per request.</summary>
    public const int PerSourceLimit = 1000;

    public override void Configure()
    {
        Get("/api/incidents");
        Definition.RequireCapability(
            DiagnosticsCapabilities.ManageClientErrors, DiagnosticsCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(IncidentsRequest req, CancellationToken ct)
    {
        var allTenants = await PlatformScope.HoldsGloballyAsync(session, User, configuration,
            DiagnosticsCapabilities.ManageClientErrors, DiagnosticsCapabilities.Defaults.LegacyRoles, ct);
        var query = new IncidentQuery(
            tenant.Slug, allTenants, DateTimeOffset.UtcNow.AddDays(-(req.Days ?? 30)), PerSourceLimit);

        var occurrences = new List<(string Source, IncidentOccurrence Row)>();
        foreach (var source in sources)
        {
            if (req.Source is not null && !string.Equals(req.Source, source.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A source is read only by a caller who could read its records directly.
            if (source.RequiredCapability is { } needed && !await capabilities.HoldsAsync(User, needed, ct))
            {
                continue;
            }

            foreach (var row in await source.ReadAsync(query, ct))
            {
                occurrences.Add((source.Name, row));
            }
        }

        var groups = occurrences
            .GroupBy(o => (o.Source, o.Row.Fingerprint))
            .Select(g => Group(g.Key.Source, g.Key.Fingerprint, g.Select(o => o.Row).ToList()))
            .Where(g => req.Resolved is not bool resolved || g.Resolved == resolved)
            .OrderByDescending(g => g.LastSeenAt)
            .ToList();

        // Only the page is redacted: the rules cost time per character, and a caller is never
        // sent the rest.
        var redactor = new DiagnosticRedactor(await SensitiveFieldNames.LoadAsync(session, allTenants, ct));
        await Send.ResponseAsync(new PaginatedResponse<IncidentGroup>
        {
            Items = groups.Skip(req.Skip).Take(req.Take).Select(g => Redacted(g, redactor)).ToList(),
            Page = req.Page,
            PageSize = req.PageSize,
            TotalItems = groups.Count,
        }, cancellation: ct);
    }

    private static IncidentGroup Redacted(IncidentGroup g, DiagnosticRedactor redactor) => new()
    {
        Source = g.Source,
        Fingerprint = g.Fingerprint,
        Title = redactor.Apply(g.Title) ?? string.Empty,
        Detail = redactor.Apply(g.Detail),
        Count = g.Count,
        FirstSeenAt = g.FirstSeenAt,
        LastSeenAt = g.LastSeenAt,
        Tenants = g.Tenants,
        Releases = g.Releases,
        Resolved = g.Resolved,
        ResolvedAt = g.ResolvedAt,
        ResolvedBy = g.ResolvedBy,
        ResolutionReference = redactor.Apply(g.ResolutionReference),
        ResolutionNote = redactor.Apply(g.ResolutionNote),
    };

    private static IncidentGroup Group(string source, string fingerprint, List<IncidentOccurrence> rows)
    {
        var latest = rows.MaxBy(r => r.LastSeenAt)!;
        var resolved = rows.All(r => r.Resolved);
        var resolution = resolved ? rows.Where(r => r.ResolvedAt is not null).MaxBy(r => r.ResolvedAt) : null;
        return new IncidentGroup
        {
            Source = source,
            Fingerprint = fingerprint,
            Title = latest.Title,
            Detail = latest.Detail,
            Count = rows.Sum(r => Math.Max(1, r.Count)),
            FirstSeenAt = rows.Min(r => r.FirstSeenAt),
            LastSeenAt = latest.LastSeenAt,
            Tenants = rows.Select(r => r.Tenant).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList(),
            Releases = rows.Select(r => r.Release).OfType<string>().Distinct(StringComparer.Ordinal).Order().ToList(),
            Resolved = resolved,
            ResolvedAt = resolution?.ResolvedAt,
            ResolvedBy = resolution?.ResolvedBy,
            ResolutionReference = resolution?.ResolutionReference,
            ResolutionNote = resolution?.ResolutionNote,
        };
    }
}
