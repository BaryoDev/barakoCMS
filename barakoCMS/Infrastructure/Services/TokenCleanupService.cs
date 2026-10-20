using Marten;
using barakoCMS.Infrastructure.Retention;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Background service that periodically cleans up expired tokens.
/// Removes expired RefreshTokens, RevokedTokens, OtpCodes and unconfirmed PendingRegistrations to
/// prevent unbounded database growth. Idempotency records have their own window and their own
/// sweep, <see cref="IdempotencyRetentionService"/>.
/// </summary>
public class TokenCleanupService(
    IServiceProvider serviceProvider,
    ILogger<TokenCleanupService> logger) : BackgroundService
{
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(1); // Run every hour

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Token cleanup service started. Cleanup interval: {Interval}", _cleanupInterval);

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
                logger.LogError(ex, "Error during token cleanup");
            }

            await Task.Delay(_cleanupInterval, stoppingToken);
        }

        logger.LogInformation("Token cleanup service stopped");
    }

    /// <summary>
    /// What is removed once its own expiry has passed. A zero window means "expired now": these rows
    /// carry their own expiry, so the cutoff is the current time.
    /// </summary>
    /// <remarks>
    /// Expired sign-in codes were never deleted by anything. OtpService only marks outstanding codes
    /// Consumed when a new one is issued, so every OTP request left a permanent row and the "this
    /// email, not consumed" scan in send and verify got slower with each one. A pending registration
    /// nobody confirmed holds a username, an address and a password hash, so keeping it after the
    /// token stopped working is data retained for no purpose.
    /// </remarks>
    internal static readonly IReadOnlyList<RetentionDeclaration> Declarations =
    [
        Expired<RefreshToken>(t => t.ExpiresAt),
        Expired<RevokedToken>(t => t.ExpiresAt),
        Expired<OtpCode>(t => t.ExpiresAt),
        Expired<PendingRegistration>(t => t.ExpiresAt),
    ];

    private static RetentionDeclaration Expired<T>(System.Linq.Expressions.Expression<Func<T, DateTime>> expiresAt)
        where T : notnull
    {
        var parameter = expiresAt.Parameters[0];
        return new RetentionDeclaration<T>(
            typeof(T).Name,
            cutoff =>
            {
                var limit = cutoff.UtcDateTime;
                return System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(
                    System.Linq.Expressions.Expression.LessThan(
                        expiresAt.Body, System.Linq.Expressions.Expression.Constant(limit)),
                    parameter);
            },
            // One tick, so the cutoff is effectively now while the sweeper still reads the window as on.
            _ => TimeSpan.FromTicks(1));
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
        using var scope = serviceProvider.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        await RetentionSweeper.SweepSessionAsync(
            session, session.TenantId, Declarations, DateTimeOffset.UtcNow, ct);

        logger.LogInformation("Token cleanup swept expired refresh tokens, revoked tokens, OTP codes, and unconfirmed registrations");
    }
}
