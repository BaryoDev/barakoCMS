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
        RuleFor(x => x.Type).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Body).MaximumLength(4000);
        RuleFor(x => x.Payload).Must(p => p is null || p.Count <= MaxPayloadKeys)
            .WithMessage($"Payload holds at most {MaxPayloadKeys} keys.");
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
        var id = await notifications.RaiseAsync(
            new RaiseNotification(req.RecipientUserId, req.Type, req.Title) { Body = req.Body, Payload = req.Payload }, ct);

        await Send.ResponseAsync(new RaiseResponse { Id = id }, 201, ct);
    }
}
