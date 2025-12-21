using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Settings;

public class GetSettingsRequest { }

public class GetSettingsResponse
{
    public List<SystemSettingDto> Settings { get; set; } = new();
}

public class SystemSettingDto
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
}

public class GetSettingsEndpoint : EndpointWithoutRequest<GetSettingsResponse>
{
    private readonly IDocumentSession _session;

    public GetSettingsEndpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Get("/api/settings");
        AllowAnonymous(); // For now - can restrict to Admin later
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var settings = await _session.Query<SystemSetting>()
            .OrderBy(s => s.Category)
            .ThenBy(s => s.Key)
            .ToListAsync(ct);

        var response = new GetSettingsResponse
        {
            Settings = settings.Select(s => new SystemSettingDto
            {
                Key = s.Key,
                Value = s.Value,
                Description = s.Description,
                Category = s.Category.ToString(),
                UpdatedAt = s.UpdatedAt
            }).ToList()
        };

        await SendAsync(response, cancellation: ct);
    }
}
