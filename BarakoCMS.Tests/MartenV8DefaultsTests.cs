using FluentAssertions;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests;

/// <summary>
/// The four Marten 8 event-store defaults this project still runs on stay restored.
/// </summary>
/// <remarks>
/// Marten 9 flipped these, and <c>EnableBigIntEvents</c> changes the type of columns on
/// <c>mt_events</c>. Production runs <c>AutoCreate.CreateOnly</c>, which refuses to alter an
/// existing table, so adopting that default is not a version bump: it is a migration, and it has to
/// be a deliberate one with a reviewed SQL file behind it.
///
/// <c>RestoreV8Defaults()</c> is one line in <c>ServiceCollectionExtensions</c> with nothing holding
/// it in place. Deleting it leaves every test green while making the next upgrade unbootable, which
/// is the failure this file exists to catch. Asserting on the resolved store rather than grepping
/// for the call means a replacement that sets the same four values individually still passes.
/// </remarks>
[Collection("Sequential")]
public class MartenV8DefaultsTests
{
    private readonly IntegrationTestFixture _fixture;

    public MartenV8DefaultsTests(IntegrationTestFixture fixture) => _fixture = fixture;

    [Fact]
    public void The_event_store_still_runs_on_the_Marten_8_defaults()
    {
        // IReadOnlyEventStoreOptions hides the two schema-bearing flags, and those are the ones
        // worth pinning, so this reads the configured graph rather than the read-only view of it.
        var events = (Marten.Events.EventGraph)_fixture.Services
            .GetRequiredService<IDocumentStore>().Options.Events;

        // Marten 9 defaults to QuickWithServerTimestamps, which stamps event timestamps in the
        // database rather than in the process appending them.
        events.AppendMode.Should().Be(EventAppendMode.Rich);

        // The schema-bearing one: bigint event columns are a table alter on mt_events.
        events.EnableBigIntEvents.Should().BeFalse();

        events.EnableAdvancedAsyncTracking.Should().BeFalse();
        events.UseIdentityMapForAggregates.Should().BeFalse();
    }
}
