namespace BarakoCMS.Notifications;

/// <summary>Bound from <c>Modules:Notifications</c>.</summary>
public sealed class NotificationsOptions
{
    public EmailDeliveryOptions EmailDelivery { get; set; } = new();
}

/// <summary>
/// Also email each notification to its recipient through whichever <c>IEmailService</c> the host
/// has (Resend, SMTP). Off by default, so installing the module sends no mail.
/// </summary>
public sealed class EmailDeliveryOptions
{
    public bool Enabled { get; set; }

    /// <summary>Only these notification types are emailed. Empty means every type.</summary>
    public string[] Types { get; set; } = [];
}
