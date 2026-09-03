using FastEndpoints;
using Marten;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;

namespace barakoCMS.Features.Settings;

internal class UpdateSettingRequest
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

internal class UpdateSettingResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

internal class UpdateSettingEndpoint : Endpoint<UpdateSettingRequest, UpdateSettingResponse>
{
    private readonly IDocumentSession _session;

    public UpdateSettingEndpoint(IDocumentSession session)
    {
        _session = session;
    }

    public override void Configure()
    {
        Post("/api/settings");
        Definition.RequireCapability(SystemCapabilities.ManageSettings, "SuperAdmin", "Admin");
    }

    /// <summary>
    /// Key fragments that mean the value is a credential, which this endpoint must not take.
    /// </summary>
    /// <remarks>
    /// Everything stored here is written in plaintext and handed back in full by
    /// <c>GET /api/settings</c>. That is fine for a feature flag and wrong for a credential, and the
    /// screen invites it: a box labelled Value next to a key called Resend:ApiKey is going to get an
    /// API key typed into it. Refusing names the endpoint that encrypts, rather than leaving the
    /// operator to discover the difference from a database dump.
    /// </remarks>
    private static readonly string[] SecretKeyFragments =
        ["apikey", "api_key", "password", "secret", "token", "credential", "privatekey"];

    public override async Task HandleAsync(UpdateSettingRequest req, CancellationToken ct)
    {
        var looksSecret = SecretKeyFragments.FirstOrDefault(
            f => req.Key.Contains(f, StringComparison.OrdinalIgnoreCase));

        if (looksSecret is not null)
        {
            ThrowError(
                $"'{req.Key}' looks like a credential, and settings stored here are kept in plaintext and "
              + "returned by GET /api/settings. Email credentials go to PUT /api/settings/email, which "
              + "encrypts them and never hands them back.", 400);
            return;
        }

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

        await Send.ResponseAsync(new UpdateSettingResponse
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
