using barakoCMS.Core.Interfaces;

namespace barakoCMS.Infrastructure.Services;

public class MockEmailService : IEmailService
{
    private readonly ILogger<MockEmailService> _logger;

    public MockEmailService(ILogger<MockEmailService> logger)
    {
        _logger = logger;
    }

    public Task SendEmailAsync(string to, string subject, string body, CancellationToken cancellationToken = default)
    {
        // Mock provider: does NOT deliver email. Log recipient + subject only (body may contain PII).
        _logger.LogWarning(
            "MockEmailService: no email provider configured — email to {To} (subject: {Subject}) was NOT sent.",
            to, subject);
        return Task.CompletedTask;
    }
}
