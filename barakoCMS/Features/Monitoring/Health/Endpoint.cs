using FastEndpoints;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace barakoCMS.Features.Monitoring.Health;

// Per-check health detail for the admin dashboard. The anonymous /health probe returns only the
// overall status; this admin-only endpoint exposes each check's name, status, and timing.
public class DetailedHealthStatus
{
    public string Status { get; set; } = "";
    public string TotalDuration { get; set; } = "";
    public Dictionary<string, HealthEntry> Entries { get; set; } = new();
}

public class HealthEntry
{
    public string Status { get; set; } = "";
    public string Duration { get; set; } = "";
    public string? Description { get; set; }
    public IReadOnlyDictionary<string, object>? Data { get; set; }
    public IEnumerable<string>? Tags { get; set; }
}

public class Endpoint : EndpointWithoutRequest<DetailedHealthStatus>
{
    private readonly HealthCheckService _healthCheckService;

    public Endpoint(HealthCheckService healthCheckService)
    {
        _healthCheckService = healthCheckService;
    }

    public override void Configure()
    {
        Get("/api/monitoring/health");
        Roles("Admin", "SuperAdmin");
        Description(b => b
            .Produces<DetailedHealthStatus>(200)
            .Produces(401)
            .Produces(403)
            .WithTags("Monitoring"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var report = await _healthCheckService.CheckHealthAsync(ct);
        var response = new DetailedHealthStatus
        {
            Status = report.Status.ToString(),
            TotalDuration = report.TotalDuration.ToString(),
            Entries = report.Entries.ToDictionary(
                e => e.Key,
                e => new HealthEntry
                {
                    Status = e.Value.Status.ToString(),
                    Duration = e.Value.Duration.ToString(),
                    Description = e.Value.Description,
                    Data = e.Value.Data,
                    Tags = e.Value.Tags,
                }),
        };
        await SendOkAsync(response, ct);
    }
}
