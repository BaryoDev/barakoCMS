using BarakoCMS.Diagnostics;
using BarakoCMS.Diagnostics.Features.Report;
using FluentAssertions;
using Marten;
using Xunit;

namespace BarakoCMS.Tests.Features.Diagnostics;

// Exercises the real dedup/reopen logic against Postgres. Uses the shared test container but its own
// Marten store + schema so it stays isolated from the app's documents.
[Collection("Sequential")]
public class ClientErrorRecorderTests
{
    private readonly IntegrationTestFixture _fixture;
    public ClientErrorRecorderTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private IDocumentStore NewStore() => DocumentStore.For(opts =>
    {
        opts.Connection(_fixture.ConnectionString);
        // Unique schema per test run keeps rows from bleeding across tests.
        opts.DatabaseSchemaName = "diag_test_" + Guid.NewGuid().ToString("N")[..8];
        new DiagnosticsModule().ConfigureMarten(opts);
    });

    private static ReportItem Fault() =>
        new() { Kind = "error", Message = "Kaboom", Source = "x.js:1", Status = 500, Severity = "error" };

    [Fact]
    public async Task Recurrence_Deduplicates_AndBumpsCount()
    {
        using var store = NewStore();
        await using var session = store.LightweightSession();

        await ClientErrorRecorder.RecordAsync(session, Fault(), "UA", null, null);
        await session.SaveChangesAsync();
        await ClientErrorRecorder.RecordAsync(session, Fault(), "UA", null, null);
        await session.SaveChangesAsync();

        var all = await session.Query<ClientError>().ToListAsync();
        all.Should().HaveCount(1);
        all[0].Count.Should().Be(2);
    }

    [Fact]
    public async Task DifferentFault_CreatesSeparateRow()
    {
        using var store = NewStore();
        await using var session = store.LightweightSession();

        await ClientErrorRecorder.RecordAsync(session, Fault(), "UA", null, null);
        var other = Fault();
        other.Message = "Different";
        await ClientErrorRecorder.RecordAsync(session, other, "UA", null, null);
        await session.SaveChangesAsync();

        (await session.Query<ClientError>().CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Recurrence_AfterResolve_ReopensIt()
    {
        using var store = NewStore();
        await using var session = store.LightweightSession();

        await ClientErrorRecorder.RecordAsync(session, Fault(), "UA", null, null);
        await session.SaveChangesAsync();

        var doc = await session.Query<ClientError>().FirstAsync();
        doc.Resolved = true;
        doc.ResolvedAt = DateTime.UtcNow;
        session.Store(doc);
        await session.SaveChangesAsync();

        await ClientErrorRecorder.RecordAsync(session, Fault(), "UA", null, null);
        await session.SaveChangesAsync();

        var reloaded = await session.Query<ClientError>().FirstAsync();
        reloaded.Resolved.Should().BeFalse();
        reloaded.ResolvedAt.Should().BeNull();
        reloaded.Count.Should().Be(2);
    }

    [Fact]
    public async Task BlankMessage_IsIgnored()
    {
        using var store = NewStore();
        await using var session = store.LightweightSession();

        await ClientErrorRecorder.RecordAsync(session, new ReportItem { Message = "   " }, "UA", null, null);
        await session.SaveChangesAsync();

        (await session.Query<ClientError>().CountAsync()).Should().Be(0);
    }
}
