using barakoCMS.Core.Interfaces;

namespace barakoCMS.Infrastructure.Services;

public class MockSmsService(ILogger<MockSmsService> logger) : ISmsService
{
    public Task SendSmsAsync(string to, string message, CancellationToken cancellationToken = default)
    {
        // No recipient here, redacted or otherwise: a phone number is personal data, and the only
        // signal an operator needs is that a send was attempted with no provider configured.
        logger.LogInformation("MockSmsService: no SMS provider configured, so the message was not sent.");
        return Task.CompletedTask;
    }
}
