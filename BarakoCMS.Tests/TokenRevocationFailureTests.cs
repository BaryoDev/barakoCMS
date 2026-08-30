using FluentAssertions;
using Marten;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// A revocation check that cannot reach its store refuses the request.
/// </summary>
/// <remarks>
/// It used to return "not revoked" for any exception at all, logged at Debug, which production does
/// not emit. So a revoked token was accepted for as long as the database was unreachable, a
/// logged-out session came back during a blip, and nothing recorded that it had happened.
///
/// The comment on that catch said it was there for a missing table on first run, which is a real
/// case and a much narrower one: with no table nothing has ever been revoked, so "not revoked" is
/// the true answer rather than a guess. That case is kept and everything else fails closed.
/// </remarks>
[Collection("Sequential")]
public class TokenRevocationFailureTests
{
    private readonly IntegrationTestFixture _factory;

    public TokenRevocationFailureTests(IntegrationTestFixture factory) => _factory = factory;

    private static barakoCMS.Infrastructure.Services.TokenRevocationService ServiceOver(IDocumentSession session) =>
        new(session,
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<barakoCMS.Infrastructure.Services.TokenRevocationService>.Instance);

    /// <summary>
    /// An unreachable store refuses rather than waving the token through.
    /// </summary>
    /// <remarks>
    /// A real store pointed at a port nothing is listening on, rather than a mocked session. The
    /// behaviour under test is what happens when the query throws for real, and a mock returning a
    /// value the real dependency never returns would not be evidence about that.
    ///
    /// A short connect timeout keeps it quick. The port is one nothing binds in this suite.
    /// </remarks>
    [Fact]
    public async Task A_revocation_check_that_cannot_reach_the_store_refuses()
    {
        await using var unreachable = DocumentStore.For(opts =>
        {
            opts.Connection("Host=127.0.0.1;Port=1;Database=nothing;Username=none;Password=none;Timeout=2;Command Timeout=2");
            opts.AutoCreateSchemaObjects = JasperFx.AutoCreate.None;
        });

        await using var session = unreachable.LightweightSession();
        var service = ServiceOver(session);

        var act = async () => await service.IsTokenRevokedAsync(Guid.NewGuid().ToString(), default);

        await act.Should().ThrowAsync<Exception>(
            "a check that could not run has not established the token is valid, and returning "
          + "\"not revoked\" accepts a revoked token for as long as the store is down");
    }

    /// <summary>
    /// The control, and the half that makes the test above mean something.
    /// </summary>
    /// <remarks>
    /// Without it, a service that threw unconditionally would satisfy the assertion above while
    /// refusing every request in the product. This is the shape of gate this project keeps being
    /// bitten by, so the working path is asserted next to the failing one.
    /// </remarks>
    [Fact]
    public async Task A_healthy_store_still_answers_both_ways()
    {
        using var scope = _factory.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var service = ServiceOver(session);

        var revoked = Guid.NewGuid().ToString();
        var untouched = Guid.NewGuid().ToString();

        session.Store(new barakoCMS.Models.RevokedToken
        {
            Id = Guid.NewGuid(),
            TokenJti = revoked,
            RevokedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
        });
        await session.SaveChangesAsync();

        (await service.IsTokenRevokedAsync(revoked, default)).Should().BeTrue("it was revoked");
        (await service.IsTokenRevokedAsync(untouched, default)).Should().BeFalse(
            "a token nobody revoked is usable, or the fix has taken the product down instead");
    }
}
