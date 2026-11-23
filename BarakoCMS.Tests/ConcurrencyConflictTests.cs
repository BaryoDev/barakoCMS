using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using barakoCMS;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// A save that loses on more than one versioned document is reported as an AggregateException of
/// conflicts, and every place that handles a lost race has to see it as one.
/// </summary>
[Collection("Integration1")]
public class ConcurrencyConflictTests
{
    private readonly IntegrationTestFixture _fixture;

    public ConcurrencyConflictTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Exception Conflict() => new JasperFx.ConcurrencyException("lost", new InvalidOperationException("inner"));

    [Fact]
    public void A_single_conflict_is_one()
    {
        ConcurrencyConflict.Is(Conflict()).Should().BeTrue();
    }

    [Fact]
    public void An_aggregate_of_two_conflicts_is_one()
    {
        ConcurrencyConflict.Is(new AggregateException(Conflict(), Conflict())).Should().BeTrue();
    }

    [Fact]
    public void A_nested_aggregate_of_conflicts_is_one()
    {
        var nested = new AggregateException(new AggregateException(Conflict(), Conflict()), Conflict());

        ConcurrencyConflict.Is(nested).Should().BeTrue();
    }

    [Fact]
    public void An_aggregate_that_also_holds_a_real_failure_is_not_one()
    {
        ConcurrencyConflict.Is(new AggregateException(Conflict(), new InvalidOperationException("boom"))).Should().BeFalse();
    }

    [Fact]
    public void An_empty_aggregate_is_not_one()
    {
        ConcurrencyConflict.Is(new AggregateException()).Should().BeFalse();
    }

    [Fact]
    public void A_plain_exception_is_not_one()
    {
        ConcurrencyConflict.Is(new InvalidOperationException("boom")).Should().BeFalse();
    }

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
