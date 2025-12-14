using FastEndpoints;
using Marten;
using barakoCMS.Models;

namespace barakoCMS.Features.Settings;

public class UpdateSettingRequest
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public class UpdateSettingResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class UpdateSettingEndpoint : Endpoint<UpdateSettingRequest, UpdateSettingResponse>
{
    private readonly IDocumentSession _session;

    public UpdateSettingEndpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Post("/api/settings");
        AllowAnonymous(); // TODO: Add [Roles("Admin")] when ready
    }

    public override async Task HandleAsync(UpdateSettingRequest req, CancellationToken ct)
    {
        // Find existing setting or create new
        var setting = await _session.Query<SystemSetting>()
            .FirstOrDefaultAsync(s => s.Key == req.Key, ct);

        if (setting == null)
        {
            // Create new setting with appropriate metadata based on key
            setting = new SystemSetting
            {
                Id = Guid.NewGuid(),
                Key = req.Key,
                Value = req.Value,
                Category = DetermineCategory(req.Key),
                Description = GetDescription(req.Key),
                UpdatedAt = DateTime.UtcNow
            };
            _session.Store(setting);
        }
        else
        {
            // Update existing
            setting.Value = req.Value;
            setting.UpdatedAt = DateTime.UtcNow;
            _session.Update(setting);
        }

        await _session.SaveChangesAsync(ct);

        await SendAsync(new UpdateSettingResponse
        {
            Success = true,
            Message = "Setting updated successfully"
        }, cancellation: ct);
    }

    private static SettingCategory DetermineCategory(string key)
    {
        if (key.StartsWith("Kubernetes")) return SettingCategory.Monitoring;
        if (key.StartsWith("HealthChecks")) return SettingCategory.Monitoring;
        if (key.StartsWith("Serilog")) return SettingCategory.Logging;
        return SettingCategory.Features;
    }

    private static string GetDescription(string key)
    {
        return key switch
        {
            "Kubernetes__Enabled" => "Enable Kubernetes cluster monitoring",
            "HealthChecksUI__Enabled" => "Enable HealthChecks UI dashboard",
            "Serilog__WriteToFile" => "Enable file-based logging",
            _ => $"Configuration for {key}"
        };
    }
}
