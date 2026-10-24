using System.Net;
using System.Net.Http.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using BarakoCMS.Tests.SampleProbe;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BarakoCMS.Tests.Numbering;

[Collection("Integration4")]
public class ExternalNumberLedgerTests(SampleProbeHost host) : IClassFixture<SampleProbeHost>
{
    private IExternalNumberLedger Ledger => host.Services.GetRequiredService<IExternalNumberLedger>();

    private static string FreshSeries() => $"ext-{Guid.NewGuid():N}"[..20];

    private async Task RecordAsync(string series, string? tenant, params long[] numbers)
    {
        await using var session = host.OpenSession(tenant);
        foreach (var n in numbers)
            Ledger.Record(session, series, n, $"ref-{n}");
        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task Gaps_SomeNumbersUnrecorded_ListsMissingRanges()
    {
        var series = FreshSeries();
        await RecordAsync(series, null, 100, 101, 104, 107);

        await using var session = host.OpenSession();
        var report = await Ledger.GapsAsync(session, series, 100, 108);

        report.Recorded.Should().Be(4);
        report.Missing.Should().Equal(new NumberRange(102, 103), new NumberRange(105, 106), new NumberRange(108, 108));
    }

    [Fact]
    public async Task Gaps_VoidedNumber_CountsAsAccountedFor()
    {
        var series = FreshSeries();
        await using (var write = host.OpenSession())
        {
            Ledger.Record(write, series, 1, "ref-1");
            Ledger.Record(write, series, 2, "torn", ExternalNumberStatus.Voided);
            await write.SaveChangesAsync();
        }

        await using var session = host.OpenSession();
        (await Ledger.GapsAsync(session, series, 1, 2)).Missing.Should().BeEmpty();
    }

    [Fact]
    public async Task Record_SameNumberTwice_FailsOnSave()
    {
        var series = FreshSeries();
        await RecordAsync(series, null, 5);

        var act = () => RecordAsync(series, null, 5);

        await act.Should().ThrowAsync<JasperFx.DocumentAlreadyExistsException>();
    }

    [Fact]
    public async Task Record_SameNumberInAnotherTenant_IsAllowed()
    {
        var series = FreshSeries();
        var tenant = await host.CreateTenantAsync();
        await RecordAsync(series, null, 5);

        var act = () => RecordAsync(series, tenant, 5);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Gaps_RangeOver100000_Throws()
    {
        await using var session = host.OpenSession();
        var act = () => Ledger.GapsAsync(session, "s", 1, 100_001);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(long.MinValue, long.MaxValue)]
    [InlineData(-1L, 5L)]
    [InlineData(0L, 100_000L)]
    public async Task Gaps_RangeNegativeOrOverflowing_Throws(long from, long to)
    {
        await using var session = host.OpenSession();
        var act = () => Ledger.GapsAsync(session, "s", from, to);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Gaps_RangeEndingAtLongMax_Completes()
    {
        var series = FreshSeries();
        await RecordAsync(series, null, long.MaxValue - 1);

        await using var session = host.OpenSession();
        var report = await Ledger.GapsAsync(session, series, long.MaxValue - 5, long.MaxValue);

        report.Recorded.Should().Be(1);
        report.Missing.Should().Equal(new NumberRange(long.MaxValue - 5, long.MaxValue - 2), new NumberRange(long.MaxValue, long.MaxValue));
    }

    [Fact]
    public async Task Gaps_StartingAtZero_Works()
    {
        var series = FreshSeries();
        await RecordAsync(series, null, 0);

        await using var session = host.OpenSession();
        (await Ledger.GapsAsync(session, series, 0, 1)).Missing.Should().Equal(new NumberRange(1, 1));
    }

    [Fact]
    public async Task Record_NegativeNumber_Throws()
    {
        await using var session = host.OpenSession();
        var act = () => Ledger.Record(session, FreshSeries(), -1, "ref");
        act.Should().Throw<ArgumentOutOfRangeException>();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Gaps_NumbersRecordedInAnotherTenant_AreNotCounted()
    {
        var series = FreshSeries();
        var tenant = await host.CreateTenantAsync();
        await RecordAsync(series, null, 1, 2, 3);

        await using var session = host.OpenSession(tenant);
        var report = await Ledger.GapsAsync(session, series, 1, 3);

        report.Recorded.Should().Be(0);
        report.Missing.Should().Equal(new NumberRange(1, 3));
    }

    [Fact]
    public async Task GapsEndpoint_WithCapability_ReturnsReport()
    {
        var series = FreshSeries();
        await RecordAsync(series, null, 1, 3);
        var client = await host.ClientHoldingAsync([SystemCapabilities.ViewNumberGaps]);

        var response = await client.GetAsync($"/api/numbering/external/{series}/gaps?from=1&to=3");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var report = await response.Content.ReadFromJsonAsync<NumberGapReport>();
        report!.Missing.Should().Equal(new NumberRange(2, 2));
    }

    [Fact]
    public async Task GapsEndpoint_WithoutCapability_Returns403()
    {
        var client = await host.ClientHoldingAsync([]);

        var response = await client.GetAsync("/api/numbering/external/any/gaps?from=1&to=3");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GapsEndpoint_ToBeforeFrom_Returns400()
    {
        var client = await host.ClientHoldingAsync([SystemCapabilities.ViewNumberGaps]);

        var response = await client.GetAsync("/api/numbering/external/any/gaps?from=10&to=3");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public void AdminDefaults_IncludeViewNumberGaps()
    {
        SystemCapabilities.DefaultsFor("Admin").Should().Contain(SystemCapabilities.ViewNumberGaps);
    }
}
