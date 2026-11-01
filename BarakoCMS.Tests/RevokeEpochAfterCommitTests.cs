using Marten;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Services;

namespace BarakoCMS.Tests;

/// <summary>
/// Ending a user's sessions drops the cached epoch once the revoking write has committed.
/// </summary>
[Collection("Integration1")]
public class RevokeEpochAfterCommitTests
{
    private readonly IntegrationTestFixture _factory;

    public RevokeEpochAfterCommitTests(IntegrationTestFixture factory) => _factory = factory;

    [Fact]
    public async Task The_cached_epoch_is_dropped_after_the_commit_and_not_before()
    {
        var userId = Guid.NewGuid();
        var epoch = Substitute.For<ISessionEpochService>();
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();

        await RevokeRefreshTokens.ForUserAsync(session, userId, "test", CancellationToken.None, epoch);

        epoch.DidNotReceive().Invalidate(userId);

        await session.SaveChangesAsync();

        epoch.Received(1).Invalidate(userId);
    }
}
