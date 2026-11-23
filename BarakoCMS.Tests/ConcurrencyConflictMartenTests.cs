using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// What Marten really throws when a save loses on more than one versioned document.
/// </summary>
[Collection("Integration1")]
public class ConcurrencyConflictMartenTests
{
    private readonly IntegrationTestFixture _fixture;

    public ConcurrencyConflictMartenTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Marten_reports_a_loss_on_two_documents_as_an_aggregate_of_conflicts()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var email = $"agg-{Guid.NewGuid():N}@example.com";
        var first = new OtpCode { Email = email, CodeHash = "a", ExpiresAt = DateTime.UtcNow.AddMinutes(10) };
        var second = new OtpCode { Email = email, CodeHash = "b", ExpiresAt = DateTime.UtcNow.AddMinutes(10) };
        await using (var seed = store.LightweightSession())
        {
            seed.Store(first, second);
            await seed.SaveChangesAsync(Ct);
        }

        // This session holds both documents at the version it loaded them.
        await using var stale = store.LightweightSession();
        var staleFirst = await stale.LoadAsync<OtpCode>(first.Id, Ct);
        var staleSecond = await stale.LoadAsync<OtpCode>(second.Id, Ct);

        // Another session changes both first.
        await using (var other = store.LightweightSession())
        {
            var a = await other.LoadAsync<OtpCode>(first.Id, Ct);
            var b = await other.LoadAsync<OtpCode>(second.Id, Ct);
            a!.Consumed = true;
            b!.Consumed = true;
            other.Update(a);
            other.Update(b);
            await other.SaveChangesAsync(Ct);
        }

        staleFirst!.Attempts = 1;
        staleSecond!.Attempts = 1;
        stale.Update(staleFirst);
        stale.Update(staleSecond);

        var thrown = await Record.ExceptionAsync(() => stale.SaveChangesAsync(Ct));

        thrown.Should().BeOfType<AggregateException>();
        ConcurrencyConflict.Is(thrown!).Should().BeTrue();
    }
}
