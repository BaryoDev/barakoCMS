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
        _logger.LogInformation("Sending Email to {To}: {Subject} - {Body}", to, subject, body);
        return Task.CompletedTask;
    }
}
