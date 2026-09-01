using Marten;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Background service that periodically cleans up expired tokens.
/// Removes expired RefreshTokens, RevokedTokens, OtpCodes, unconfirmed PendingRegistrations and old
/// IdempotencyRecords to prevent
/// unbounded database growth.
/// </summary>
public class TokenCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TokenCleanupService> _logger;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(1); // Run every hour

    public TokenCleanupService(IServiceProvider serviceProvider, ILogger<TokenCleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Token cleanup service started. Cleanup interval: {Interval}", _cleanupInterval);

        // Initial delay to let the application warm up
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupExpiredTokensAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Graceful shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during token cleanup");
            }

            await Task.Delay(_cleanupInterval, stoppingToken);
        }

        _logger.LogInformation("Token cleanup service stopped");
    }

    /// <summary>
    /// One sweep. Internal so a test can run it directly instead of waiting on the timer.
    /// </summary>
    /// <remarks>
    /// Every pass is a <c>DeleteWhere</c>, which is one DELETE statement per document type. The
    /// previous shape loaded the full expired set into memory and deleted row by row, which is the
    /// work this service exists to avoid doing.
    /// </remarks>
    internal async Task CleanupExpiredTokensAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var now = DateTime.UtcNow;
        var idempotencyCutoff = now.AddHours(-24);

        session.DeleteWhere<RefreshToken>(t => t.ExpiresAt < now);
        session.DeleteWhere<RevokedToken>(t => t.ExpiresAt < now);
        session.DeleteWhere<IdempotencyRecord>(r => r.CreatedAt < idempotencyCutoff);

        // Expired sign-in codes were never deleted by anything. OtpService only marks outstanding
        // codes Consumed when a new one is issued, so every OTP request left a permanent row and the
        // "this email, not consumed" scan in send and verify got slower with each one. The ExpiresAt
        // index is already registered, so this pass costs an indexed delete.
        session.DeleteWhere<OtpCode>(o => o.ExpiresAt < now);

        // Same shape, same reason. A pending registration nobody confirmed is dead weight, and it
        // holds a username, an address and a password hash, so keeping it after the token stopped
        // working is data retained for no purpose.
        session.DeleteWhere<PendingRegistration>(p => p.ExpiresAt < now);

        await session.SaveChangesAsync(ct);

        _logger.LogInformation("Token cleanup swept expired refresh tokens, revoked tokens, OTP codes, unconfirmed registrations and idempotency records older than {Cutoff}", idempotencyCutoff);
    }
}
