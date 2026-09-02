using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// The cleanup sweep has to reach every document type that grows without bound.
/// </summary>
/// <remarks>
/// It swept RefreshToken, RevokedToken and IdempotencyRecord and never touched OtpCode, and no
/// other deletion path existed. <c>OtpService.SendCodeAsync</c> only marks outstanding codes
/// Consumed when a new one is issued, so every sign-in request left a permanent row behind and the
/// "this email, not consumed" scan in send and verify degraded with the table. The ExpiresAt index
/// was already registered, so the pass was the only missing part.
/// </remarks>
[Collection("Sequential")]
public class TokenCleanupTests
{
    private readonly IntegrationTestFixture _factory;

    public TokenCleanupTests(IntegrationTestFixture factory) => _factory = factory;

    private async Task SweepAsync()
    {
        var service = new TokenCleanupService(_factory.Services, NullLogger<TokenCleanupService>.Instance);
        await service.CleanupExpiredTokensAsync(TestContext.Current.CancellationToken);
    }

    private async Task StoreAsync(params object[] documents)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.StoreObjects(documents);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<T?> LoadAsync<T>(Guid id) where T : class
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.LoadAsync<T>(id, TestContext.Current.CancellationToken);
    }

    /// <summary>IdempotencyRecord is keyed by its string Key, not a Guid.</summary>
    private async Task<IdempotencyRecord?> LoadByKeyAsync(string key)
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        return await session.LoadAsync<IdempotencyRecord>(key, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Expired_otp_codes_are_deleted()
    {
        var expired = new OtpCode
        {
            Id = Guid.NewGuid(),
            Email = $"sweep-{Guid.NewGuid():N}@example.com",
            CodeHash = "irrelevant",
            ExpiresAt = DateTime.UtcNow.AddHours(-2),
        };
        await StoreAsync(expired);

        await SweepAsync();

        (await LoadAsync<OtpCode>(expired.Id)).Should().BeNull(
            "nothing else deletes an OtpCode, so every sign-in request left a permanent row and the "
            + "not-consumed scan in send and verify got slower with each one");
    }

    [Fact]
    public async Task A_live_otp_code_survives_the_sweep()
    {
        var live = new OtpCode
        {
            Id = Guid.NewGuid(),
            Email = $"sweep-{Guid.NewGuid():N}@example.com",
            CodeHash = "irrelevant",
            ExpiresAt = DateTime.UtcNow.AddMinutes(10),
        };
        await StoreAsync(live);

        await SweepAsync();

        (await LoadAsync<OtpCode>(live.Id)).Should().NotBeNull(
            "a code someone is about to type must not be swept out from under them");
    }

    /// <summary>
    /// The three passes that already existed still do their job after being rewritten as
    /// <c>DeleteWhere</c>, and still leave unexpired rows alone.
    /// </summary>
    [Fact]
    public async Task The_existing_passes_still_delete_what_expired_and_keep_what_did_not()
    {
        var expiredRefresh = new RefreshToken
        {
            Id = Guid.NewGuid(),
            Token = $"expired-{Guid.NewGuid():N}",
            UserId = Guid.NewGuid(),
            ExpiresAt = DateTime.UtcNow.AddDays(-1),
        };
        var liveRefresh = new RefreshToken
        {
            Id = Guid.NewGuid(),
            Token = $"live-{Guid.NewGuid():N}",
            UserId = Guid.NewGuid(),
            ExpiresAt = DateTime.UtcNow.AddDays(1),
        };
        var expiredRevoked = new RevokedToken
        {
            Id = Guid.NewGuid(),
            TokenJti = $"expired-{Guid.NewGuid():N}",
            ExpiresAt = DateTime.UtcNow.AddDays(-1),
        };
        var oldIdempotency = new IdempotencyRecord
        {
            Key = $"old-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow.AddHours(-48),
        };
        var freshIdempotency = new IdempotencyRecord
        {
            Key = $"fresh-{Guid.NewGuid():N}",
            CreatedAt = DateTime.UtcNow,
        };

        await StoreAsync(expiredRefresh, liveRefresh, expiredRevoked, oldIdempotency, freshIdempotency);

        await SweepAsync();

        (await LoadAsync<RefreshToken>(expiredRefresh.Id)).Should().BeNull();
        (await LoadAsync<RefreshToken>(liveRefresh.Id)).Should().NotBeNull();
        (await LoadAsync<RevokedToken>(expiredRevoked.Id)).Should().BeNull();
        (await LoadByKeyAsync(oldIdempotency.Key)).Should().BeNull();
        (await LoadByKeyAsync(freshIdempotency.Key)).Should().NotBeNull();
    }
}
