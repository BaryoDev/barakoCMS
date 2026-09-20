using barakoCMS.Core.Interfaces;

namespace barakoCMS.Infrastructure.Services;

public class MockEmailService(ILogger<MockEmailService> logger) : IEmailService
{
    public Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        // Mock provider: does NOT deliver email. No recipient or subject here: an address is
        // personal data and a subject line routinely carries it too ("Your test results", "Invoice
        // for ..."). The only signal an operator needs is that a send was attempted with no
        // provider configured.
        logger.LogWarning("MockEmailService: no email provider configured, so the email was not sent.");
        return Task.CompletedTask;
    }
}
