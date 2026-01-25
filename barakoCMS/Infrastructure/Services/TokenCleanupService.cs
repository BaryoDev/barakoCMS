using Marten;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Background service that periodically cleans up expired tokens.
/// Removes expired RefreshTokens and RevokedTokens to prevent unbounded database growth.
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

    private async Task CleanupExpiredTokensAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        var now = DateTime.UtcNow;

        // Delete expired refresh tokens
        var expiredRefreshTokens = await session.Query<RefreshToken>()
            .Where(t => t.ExpiresAt < now)
            .ToListAsync(ct);

        if (expiredRefreshTokens.Count > 0)
        {
            foreach (var token in expiredRefreshTokens)
            {
                session.Delete(token);
            }
            _logger.LogInformation("Deleted {Count} expired refresh tokens", expiredRefreshTokens.Count);
        }

        // Delete expired revoked tokens
        var expiredRevokedTokens = await session.Query<RevokedToken>()
            .Where(t => t.ExpiresAt < now)
            .ToListAsync(ct);

        if (expiredRevokedTokens.Count > 0)
        {
            foreach (var token in expiredRevokedTokens)
            {
                session.Delete(token);
            }
            _logger.LogInformation("Deleted {Count} expired revoked tokens", expiredRevokedTokens.Count);
        }

        // Delete old idempotency records (older than 24 hours)
        var oldIdempotencyRecords = await session.Query<IdempotencyRecord>()
            .Where(r => r.CreatedAt < now.AddHours(-24))
            .ToListAsync(ct);

        if (oldIdempotencyRecords.Count > 0)
        {
            foreach (var record in oldIdempotencyRecords)
            {
                session.Delete(record);
            }
            _logger.LogInformation("Deleted {Count} old idempotency records", oldIdempotencyRecords.Count);
        }

        if (expiredRefreshTokens.Count > 0 || expiredRevokedTokens.Count > 0 || oldIdempotencyRecords.Count > 0)
        {
            await session.SaveChangesAsync(ct);
        }
    }
}
