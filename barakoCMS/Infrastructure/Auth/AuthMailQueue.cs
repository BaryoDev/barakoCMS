using System.Threading.Channels;
using barakoCMS.Core.Interfaces;

namespace barakoCMS.Infrastructure.Auth;

/// <summary>
/// Sends the mail an anonymous sign-in request produces in the background, so the request answers
/// in the same time whether or not the address has an account.
/// </summary>
/// <remarks>
/// Same shape as <see cref="LockoutNoticeSender"/>: bounded, one message at a time, each from its
/// own DI scope, a failure logged and not retried. When the queue is full the message is dropped
/// and logged rather than the request waiting. Messages still queued when the host stops are not
/// sent.
/// </remarks>
public sealed class AuthMailQueue(
    IServiceScopeFactory scopes,
    ILogger<AuthMailQueue> logger) : BackgroundService
{
    internal const int Capacity = 1000;

    internal static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    private readonly Channel<(string To, string Subject, string Body)> _queue =
        Channel.CreateBounded<(string, string, string)>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

    public void Enqueue(string to, string subject, string body)
    {
        if (!_queue.Writer.TryWrite((to, subject, body)))
        {
            logger.LogError("The sign-in mail queue is full; a message was not sent");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var (to, subject, body) in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(SendTimeout);

                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var sender = scope.ServiceProvider.GetRequiredService<IEmailService>();
                    await sender.SendEmailAsync(to, subject, body, timeout.Token);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to send a sign-in email");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
