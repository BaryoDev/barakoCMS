using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;

namespace barakoCMS.Features.Settings;

internal class GetSettingsRequest { }

internal class SystemSettingDto
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
}

internal class GetSettingsEndpoint : Endpoint<ListRequest, PaginatedResponse<SystemSettingDto>>
{
    private readonly IDocumentSession _session;

    public GetSettingsEndpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Get("/api/settings");
        Definition.RequireCapability(SystemCapabilities.ManageSettings, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(ListRequest req, CancellationToken ct)
    {
        var page = await _session.Query<SystemSetting>()
            .OrderBy(s => s.Category)
            .ThenBy(s => s.Key)
            .ToPagedResponseAsync(req, ct);

        await Send.ResponseAsync(new PaginatedResponse<SystemSettingDto>
        {
            Items = page.Items.Select(s => new SystemSettingDto
            {
                Key = s.Key,
                Value = s.Value,
                Description = s.Description,
                Category = s.Category.ToString(),
                UpdatedAt = s.UpdatedAt
            }).ToList(),
            Page = page.Page,
            PageSize = page.PageSize,
            TotalItems = page.TotalItems,
        }, cancellation: ct);
    }
}
