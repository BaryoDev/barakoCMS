namespace barakoCMS.Core.Interfaces;

public interface ISmsService
{
    Task SendSmsAsync(string to, string message, CancellationToken cancellationToken = default);
}
