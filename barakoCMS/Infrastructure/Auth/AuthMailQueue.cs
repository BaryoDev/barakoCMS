using System.Threading.Channels;
using barakoCMS.Core.Interfaces;
using Prometheus;

namespace barakoCMS.Infrastructure.Auth;

/// <summary>
/// Sends the mail an anonymous sign-in request produces in the background, so the request answers
/// in the same time whether or not the address has an account.
/// </summary>
/// <remarks>
/// Same shape as <see cref="LockoutNoticeSender"/>: bounded, each send from its own DI scope, a
/// failure logged and not retried. One message is held per address, and a newer one replaces the
/// queued one, since a newer code or link supersedes the old. When the queue is full the message is
/// dropped, counted and logged (without the address) rather than the request waiting. Messages
/// still queued when the host stops are not sent.
/// </remarks>
public sealed class AuthMailQueue(
    IServiceScopeFactory scopes,
    ILogger<AuthMailQueue> logger) : BackgroundService
{
    internal const int Capacity = 1000;
    internal const int Readers = 4;

    internal static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    internal static readonly Counter DroppedCounter = Metrics.CreateCounter(
        "barakocms_auth_mail_dropped_total",
        "Sign-in mail not sent because the queue was full.");

    private readonly object _gate = new();
    private readonly Dictionary<string, (string Subject, string Body)> _pending = new();
    private readonly Channel<string> _order = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = false });

    internal int PendingCount { get { lock (_gate) return _pending.Count; } }

    internal string? PendingBody(string to)
    {
        lock (_gate) return _pending.TryGetValue(to.ToLowerInvariant(), out var m) ? m.Body : null;
    }

    public void Enqueue(string to, string subject, string body)
    {
        var key = to.ToLowerInvariant();
        lock (_gate)
        {
            if (_pending.ContainsKey(key))
            {
                _pending[key] = (subject, body);
                return;
            }

            if (_pending.Count < Capacity)
            {
                _pending[key] = (subject, body);
                _order.Writer.TryWrite(key);
                return;
            }
        }

        DroppedCounter.Inc();
        logger.LogError("The sign-in mail queue is full; a message was not sent");
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, Readers).Select(_ => ReadAsync(stoppingToken)));

    private async Task ReadAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var key in _order.Reader.ReadAllAsync(stoppingToken))
            {
                (string Subject, string Body) message;
                lock (_gate)
                {
                    if (!_pending.Remove(key, out message)) continue;
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(SendTimeout);

                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var sender = scope.ServiceProvider.GetRequiredService<IEmailService>();
                    await sender.SendEmailAsync(key, message.Subject, message.Body, timeout.Token);
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
