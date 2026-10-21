using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using barakoCMS.Features.Audit.List;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Audit;

/// <summary>
/// GET /api/audit?since= is the pull an outside SIEM polls: oldest first, bounded pages, and a cursor
/// that resumes exactly where the last response stopped.
/// </summary>
[Collection("Sequential")]
public class AuditPollTests
{
    private readonly IntegrationTestFixture _factory;

    public AuditPollTests(IntegrationTestFixture factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task SeedAsync(params AuditEvent[] events)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        using var session = store.LightweightSession();
        foreach (var e in events) session.Store(e);
        await session.SaveChangesAsync(Ct);
    }

    private async Task<HttpResponseMessage> GetAsync(string role, string query)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await _factory.StoredUserTokenAsync(role));
        return await client.GetAsync($"/api/audit{query}", Ct);
    }

    private async Task<AuditListResponse> PageAsync(string role, string query)
    {
        var response = await GetAsync(role, query);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<AuditListResponse>(Ct))!;
    }

    private static List<AuditEvent> Entries(string tenant, int count, DateTime first, bool sameInstant = false) =>
        Enumerable.Range(0, count).Select(i => new AuditEvent
        {
            TenantSlug = tenant,
            Action = $"test.poll.{i}",
            CreatedAt = sameInstant ? first : first.AddMinutes(i),
        }).ToList();

    [Fact]
    public async Task Following_the_cursor_reads_every_entry_once_oldest_first()
    {
        var tenant = $"poll_{Guid.NewGuid():N}";
        var entries = Entries(tenant, 5, DateTime.UtcNow.AddHours(-3));
        await SeedAsync(entries.ToArray());

        var seen = new List<Guid>();
        var cursor = entries[0].CreatedAt.AddSeconds(-1).ToString("O");
        for (var i = 0; i < 10; i++)
        {
            var page = await PageAsync("SuperAdmin", $"?tenant={tenant}&pageSize=2&since={Uri.EscapeDataString(cursor)}");
            page.Cursor.Should().NotBeNullOrEmpty();
            if (page.Items.Count == 0) break;
            page.Items.Count.Should().BeLessThanOrEqualTo(2);
            seen.AddRange(page.Items.Select(e => e.Id));
            cursor = page.Cursor!;
        }

        seen.Should().HaveCount(5);
        seen.Should().Equal(entries.Select(e => e.Id), "oldest first, each once");
    }

    /// <summary>
    /// A time with a seventh digit of 5 that Postgres stores rounded down. Whether a tie rounds down
    /// depends on the double the fraction parses to, so Postgres is asked instead of guessed at.
    /// </summary>
    private async Task<DateTime> TieThatPostgresRoundsDownAsync()
    {
        var baseTime = DateTime.UtcNow.AddHours(-3);
        baseTime = new DateTime(baseTime.Ticks - baseTime.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

        await using var connection = new Npgsql.NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(Ct);
        for (var micro = 0; micro < 200; micro++)
        {
            var tie = baseTime.AddTicks(micro * 10 + 5);
            var cut = baseTime.AddTicks(micro * 10);
            await using var command = new Npgsql.NpgsqlCommand("SELECT (@tie)::timestamptz = (@cut)::timestamptz", connection);
            command.Parameters.AddWithValue("tie", tie.ToString("O"));
            command.Parameters.AddWithValue("cut", cut.ToString("O"));
            if ((bool)(await command.ExecuteScalarAsync(Ct))!) return tie;
        }

        throw new Xunit.Sdk.XunitException("Postgres rounded every candidate tie up, so there is no tie to test with.");
    }

    [Fact]
    public async Task Entries_written_at_one_instant_are_not_skipped_or_repeated_across_pages()
    {
        var tenant = $"poll_{Guid.NewGuid():N}";
        var at = await TieThatPostgresRoundsDownAsync();
        var entries = Entries(tenant, 5, at, sameInstant: true);
        await SeedAsync(entries.ToArray());

        var seen = new List<Guid>();
        var cursor = entries[0].CreatedAt.AddSeconds(-1).ToString("O");
        for (var i = 0; i < 10; i++)
        {
            var page = await PageAsync("SuperAdmin", $"?tenant={tenant}&pageSize=2&since={Uri.EscapeDataString(cursor)}");
            if (page.Items.Count == 0) break;
            seen.AddRange(page.Items.Select(e => e.Id));
            cursor = page.Cursor!;
        }

        seen.Should().HaveCount(5);
        seen.Should().OnlyHaveUniqueItems();
        seen.Should().BeEquivalentTo(entries.Select(e => e.Id));
    }

    [Fact]
    public async Task A_cursor_taken_at_the_end_resumes_with_only_what_was_written_after_it()
    {
        var tenant = $"poll_{Guid.NewGuid():N}";
        var old = Entries(tenant, 3, DateTime.UtcNow.AddHours(-3));
        await SeedAsync(old.ToArray());

        var start = old[0].CreatedAt.AddSeconds(-1).ToString("O");
        var drained = await PageAsync("SuperAdmin", $"?tenant={tenant}&pageSize=100&since={Uri.EscapeDataString(start)}");
        drained.Items.Should().HaveCount(3);

        var later = new AuditEvent { TenantSlug = tenant, Action = "test.poll.later", CreatedAt = DateTime.UtcNow.AddHours(-2) };
        await SeedAsync(later);

        var resumed = await PageAsync("SuperAdmin", $"?tenant={tenant}&since={Uri.EscapeDataString(drained.Cursor!)}");
        resumed.Items.Should().ContainSingle().Which.Id.Should().Be(later.Id);
    }

    [Fact]
    public async Task Without_since_the_list_is_newest_first_and_carries_no_cursor()
    {
        var tenant = $"poll_{Guid.NewGuid():N}";
        var entries = Entries(tenant, 3, DateTime.UtcNow.AddHours(-3));
        await SeedAsync(entries.ToArray());

        var page = await PageAsync("SuperAdmin", $"?tenant={tenant}");

        page.Cursor.Should().BeNull();
        page.Items.Select(e => e.Id).Should().Equal(entries.AsEnumerable().Reverse().Select(e => e.Id));
    }

    [Fact]
    public async Task A_since_that_is_not_a_cursor_is_refused()
    {
        var response = await GetAsync("SuperAdmin", "?since=banana");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_tenant_admin_polling_reads_only_its_own_tenants_entries()
    {
        var other = $"poll_{Guid.NewGuid():N}";
        var entries = Entries(other, 2, DateTime.UtcNow.AddHours(-3));
        await SeedAsync(entries.ToArray());
        var since = Uri.EscapeDataString(entries[0].CreatedAt.AddSeconds(-1).ToString("O"));

        var page = await PageAsync("Admin", $"?tenant={other}&since={since}");

        page.Items.Should().NotContain(e => e.TenantSlug == other);
    }
}
