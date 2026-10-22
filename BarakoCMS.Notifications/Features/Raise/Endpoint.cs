using barakoCMS.Infrastructure.Auth;
using FastEndpoints;
using FluentValidation;

namespace BarakoCMS.Notifications.Features.Raise;

internal sealed class RaiseRequest
{
    public Guid RecipientUserId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Body { get; set; }
    public Dictionary<string, object>? Payload { get; set; }
}

internal sealed class RaiseResponse
{
    public Guid Id { get; set; }
}

internal sealed class RaiseValidator : Validator<RaiseRequest>
{
    internal const int MaxPayloadKeys = 50;

    public RaiseValidator()
    {
        RuleFor(x => x.RecipientUserId).NotEmpty();
        RuleFor(x => x.Type).NotEmpty().MaximumLength(100)
            .Must(t => !NotificationRules.HasControlCharacter(t)).WithMessage("Type cannot hold control characters.");
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200)
            .Must(t => !NotificationRules.HasControlCharacter(t)).WithMessage("Title cannot hold control characters.");
        RuleFor(x => x.Body).MaximumLength(4000);
        RuleFor(x => x.Payload).Must(p => p is null || p.Count <= MaxPayloadKeys)
            .WithMessage($"Payload holds at most {MaxPayloadKeys} keys.");
        RuleFor(x => x.Payload).Must(p => !NotificationRules.PayloadTooLarge(p))
            .WithMessage($"Payload is at most {NotificationRules.MaxPayloadBytes} bytes once serialized.");
    }
}

/// <summary>POST /api/notifications. Raises a notification for a user of the current tenant.</summary>
internal sealed class Endpoint(INotificationService notifications) : Endpoint<RaiseRequest, RaiseResponse>
{
    public override void Configure()
    {
        Post("/api/notifications");
        Definition.RequireCapability(
            NotificationsCapabilities.ManageNotifications, NotificationsCapabilities.Defaults.LegacyRoles);
    }

    public override async Task HandleAsync(RaiseRequest req, CancellationToken ct)
    {
        Guid id;
        try
        {
            id = await notifications.RaiseAsync(
                new RaiseNotification(req.RecipientUserId, req.Type, req.Title) { Body = req.Body, Payload = req.Payload }, ct);
        }
        catch (RecipientNotInTenantException)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.ResponseAsync(new RaiseResponse { Id = id }, 201, ct);
    }
}
