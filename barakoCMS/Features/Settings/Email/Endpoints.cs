using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Audit;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.Settings.Email;

/// <summary>
/// What is configured, and where each value came from. Never the key itself.
/// </summary>
/// <remarks>
/// There is no field here that could hold the API key, which is the point. An admin screen that
/// repopulates the box with the real secret puts it in every browser cache, every screen share and
/// every proxy log, and a response shape with nowhere to put it cannot be made to do that by a later
/// change that forgets why.
/// </remarks>
internal sealed class EmailSettingsResponse
{
    public bool ApiKeySet { get; set; }
    public string ApiKeySource { get; set; } = nameof(EmailSettingSource.None);
    public string FromAddress { get; set; } = string.Empty;
    public string FromAddressSource { get; set; } = nameof(EmailSettingSource.None);
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    /// <summary>Whether a real provider is registered, or email is going nowhere.</summary>
    public bool ProviderRegistered { get; set; }
}

internal sealed class GetEmailSettingsEndpoint : EndpointWithoutRequest<EmailSettingsResponse>
{
    private readonly IQuerySession _session;
    private readonly IEmailSettingsProvider _provider;
    private readonly IEmailService _email;

    public GetEmailSettingsEndpoint(IQuerySession session, IEmailSettingsProvider provider, IEmailService email)
    {
        _session = session;
        _provider = provider;
        _email = email;
    }

    public override void Configure()
    {
        Get("/api/settings/email");
        // The summary, which reports whether a key is set rather than what it is. Same tier as the
        // rest of settings, and the write below is deliberately not.
        Definition.RequireCapability(SystemCapabilities.ManageSettings, "SuperAdmin", "Admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var resolved = await _provider.GetAsync(ct);
        var stored = await _session.LoadAsync<EmailSettings>(EmailSettings.SingletonId, ct);

        await Send.ResponseAsync(new EmailSettingsResponse
        {
            ApiKeySet = !string.IsNullOrEmpty(resolved.ApiKey),
            ApiKeySource = resolved.ApiKeySource.ToString(),
            FromAddress = resolved.FromAddress ?? string.Empty,
            FromAddressSource = resolved.FromAddressSource.ToString(),
            UpdatedAt = stored?.UpdatedAt,
            UpdatedBy = stored?.UpdatedBy,
            ProviderRegistered = !EmailProvider.IsMock(_email),
        }, cancellation: ct);
    }
}

internal sealed class UpdateEmailSettingsRequest
{
    /// <summary>
    /// The new key. Null leaves the stored one alone; empty clears it, so configuration takes over
    /// again.
    /// </summary>
    /// <remarks>
    /// Null and empty mean different things here because the screen cannot show the current value,
    /// so it has no way to send it back unchanged. Treating an absent field as "clear it" would wipe
    /// the key every time somebody edited the From address.
    /// </remarks>
    public string? ApiKey { get; set; }

    /// <summary>Null leaves it alone; empty clears it.</summary>
    public string? FromAddress { get; set; }
}

internal sealed class UpdateEmailSettingsEndpoint : Endpoint<UpdateEmailSettingsRequest, EmailSettingsResponse>
{
    private readonly IDocumentSession _session;
    private readonly ISecretProtector _protector;
    private readonly IEmailSettingsProvider _provider;
    private readonly IEmailService _email;
    private readonly barakoCMS.Infrastructure.Multitenancy.TenantContext _tenant;

    public UpdateEmailSettingsEndpoint(
        IDocumentSession session,
        ISecretProtector protector,
        IEmailSettingsProvider provider,
        IEmailService email,
        barakoCMS.Infrastructure.Multitenancy.TenantContext tenant)
    {
        _session = session;
        _protector = protector;
        _provider = provider;
        _email = email;
        _tenant = tenant;
    }

    public override void Configure()
    {
        Put("/api/settings/email");
        // Its own capability, and SuperAdmin as the only legacy fallback. Changing where the
        // system's email comes from redirects every password reset and every verification token in
        // the deployment, which is a takeover rather than an administrative tweak, and it is exactly
        // the change a compromised admin account makes.
        Definition.RequireCapability(SystemCapabilities.ManageEmailSettings, "SuperAdmin");
    }

    public override async Task HandleAsync(UpdateEmailSettingsRequest req, CancellationToken ct)
    {
        var settings = await _session.LoadAsync<EmailSettings>(EmailSettings.SingletonId, ct)
            ?? new EmailSettings();

        var changed = new List<string>();

        if (req.ApiKey is not null)
        {
            var trimmed = req.ApiKey.Trim();
            var next = trimmed.Length == 0 ? string.Empty : _protector.Protect(trimmed);

            // Compared on whether there is a key rather than on the ciphertext, which is different
            // every time it is encrypted because the nonce is.
            if (string.IsNullOrEmpty(settings.ProtectedApiKey) != (next.Length == 0) || next.Length > 0)
            {
                settings.ProtectedApiKey = next;
                changed.Add(next.Length == 0 ? "apiKey.cleared" : "apiKey.set");
            }
        }

        if (req.FromAddress is not null)
        {
            var trimmed = req.FromAddress.Trim();
            if (!string.Equals(settings.FromAddress, trimmed, StringComparison.Ordinal))
            {
                settings.FromAddress = trimmed;
                changed.Add("fromAddress");
            }
        }

        if (changed.Count > 0)
        {
            settings.UpdatedAt = DateTime.UtcNow;
            settings.UpdatedBy = User.FindFirst("Username")?.Value ?? User.Identity?.Name;
            _session.Store(settings);

            // What changed, never what it changed to. An audit entry that quotes the key puts the
            // key in the audit trail, which is the one table designed never to be deleted from.
            Guid? actorId = Guid.TryParse(User.FindFirst("UserId")?.Value, out var id) ? id : null;
            await AuditLog.RecordAsync(_session, _tenant.Slug, "settings.email.changed",
                actorId, settings.UpdatedBy,
                targetType: nameof(EmailSettings), targetId: EmailSettings.SingletonId.ToString(),
                metadata: new() { ["fields"] = string.Join(", ", changed) },
                ct: ct);

            await _session.SaveChangesAsync(ct);
        }

        var resolved = await _provider.GetAsync(ct);

        await Send.ResponseAsync(new EmailSettingsResponse
        {
            ApiKeySet = !string.IsNullOrEmpty(resolved.ApiKey),
            ApiKeySource = resolved.ApiKeySource.ToString(),
            FromAddress = resolved.FromAddress ?? string.Empty,
            FromAddressSource = resolved.FromAddressSource.ToString(),
            UpdatedAt = settings.UpdatedAt,
            UpdatedBy = settings.UpdatedBy,
            ProviderRegistered = !EmailProvider.IsMock(_email),
        }, cancellation: ct);
    }
}

internal sealed class SendTestEmailResponse
{
    public bool Sent { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Sends one email to the caller's own address, so a configuration screen can say whether it worked.
/// </summary>
/// <remarks>
/// To the caller and nowhere else, deliberately. An endpoint that takes a recipient is a way to send
/// mail from this deployment's domain to any address somebody names, and the person who needs to see
/// the test is the one who just typed the credentials in.
///
/// It refuses when no provider is registered rather than reporting success. The mock provider logs
/// and returns, so a test button in front of it answers "sent" every time and moves the failure to
/// the first real invoice, which is the thing this endpoint exists to prevent.
/// </remarks>
internal sealed class SendTestEmailEndpoint : EndpointWithoutRequest<SendTestEmailResponse>
{
    private readonly IEmailService _email;
    private readonly IEmailSettingsProvider _provider;
    private readonly IQuerySession _session;
    private readonly IConfiguration _config;
    private readonly ILogger<SendTestEmailEndpoint> _logger;

    public SendTestEmailEndpoint(
        IEmailService email,
        IEmailSettingsProvider provider,
        IQuerySession session,
        IConfiguration config,
        ILogger<SendTestEmailEndpoint> logger)
    {
        _email = email;
        _provider = provider;
        _session = session;
        _config = config;
        _logger = logger;
    }

    public override void Configure()
    {
        Post("/api/settings/email/test");
        // Sends real mail through the configured provider, so it is the write gate rather than the
        // read one.
        Definition.RequireCapability(SystemCapabilities.ManageEmailSettings, "SuperAdmin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (EmailProvider.IsMock(_email))
        {
            ThrowError(
                "No email provider is registered, so nothing would be delivered. Add a provider module, "
              + "BarakoCMS.Email.Smtp or BarakoCMS.Email.Resend, and restart.", 400);
            return;
        }

        var resolved = await _provider.GetAsync(ct);
        if (string.IsNullOrEmpty(resolved.ApiKey))
        {
            ThrowError("No API key is set, in the admin or in configuration.", 400);
            return;
        }

        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId))
        {
            ThrowError("No user id on the token.", 400);
            return;
        }

        var user = await _session.LoadAsync<User>(userId, ct);
        if (user is null || string.IsNullOrWhiteSpace(user.Email))
        {
            ThrowError("Your account has no email address to send the test to.", 400);
            return;
        }

        var appName = _config["Branding:AppName"] ?? "BarakoCMS";

        try
        {
            await _email.SendEmailAsync(
                user.Email,
                $"{appName} email test",
                $"<p>This is a test from {appName}. If you are reading it, email is configured and "
              + "delivering.</p>",
                ct);
        }
        catch (Exception ex)
        {
            // The provider's own reason, which is the whole value of a test button: "failed" alone
            // sends the operator back to guessing which of the two fields is wrong.
            _logger.LogWarning(ex, "The email test send failed");
            ThrowError($"The provider refused it: {ex.Message}", 400);
            return;
        }

        await Send.ResponseAsync(new SendTestEmailResponse
        {
            Sent = true,
            Message = $"Sent to {user.Email}.",
        }, cancellation: ct);
    }
}

/// <summary>Whether the registered provider actually delivers anything.</summary>
internal static class EmailProvider
{
    internal static bool IsMock(IEmailService email) =>
        email is barakoCMS.Infrastructure.Services.MockEmailService;
}
