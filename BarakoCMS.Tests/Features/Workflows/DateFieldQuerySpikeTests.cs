using System.Globalization;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Marten.Linq.MatchesSql;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// Pins the query a date-relative sweep uses to find entries by a date field held in their data:
/// one SQL read that cannot throw on a value that is missing, empty or not a date, and an exact
/// check on what it returns.
/// </summary>
/// <remarks>
/// The SQL only narrows by the first ten characters of the text, compared as text, so no cast can
/// fail on a value like 2026-02-30. The window is widened by a day each side, because an offset in
/// the text can move the UTC date by that much, and the exact window is applied after the read.
/// </remarks>
[Collection("Integration4")]
public class DateFieldQuerySpikeTests(IntegrationTestFixture factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string PrefilterSql =
        @"substr(d.data -> 'Data' ->> ?, 1, 10) COLLATE ""C"" >= ? AND substr(d.data -> 'Data' ->> ?, 1, 10) COLLATE ""C"" <= ?";

    [Fact]
    public async Task One_query_returns_only_the_entries_dated_in_the_window()
    {
        var type = $"Appointment{Guid.NewGuid():N}"[..24];
        var inside = await StoreAsync(type, "2026-10-16T09:00:00Z");
        await StoreAsync(type, "soon");
        await StoreAsync(type, "");
        await StoreAsync(type, null);
        await StoreAsync(type, "2026-02-30T09:00:00Z");
        await StoreAsync(type, "2026-10-20T09:00:00Z");
        var from = DateTimeOffset.Parse("2026-10-16T00:00:00Z", CultureInfo.InvariantCulture);
        var to = from.AddDays(1);

        var found = await ReadWindowAsync(type, "startsAt", from, to);

        found.Should().Equal(inside);
    }

    [Fact]
    public async Task An_offset_in_the_text_is_compared_as_an_instant()
    {
        var type = $"Appointment{Guid.NewGuid():N}"[..24];
        // 2026-10-17T01:00 at +08:00 is 2026-10-16T17:00Z, inside the window though its date text is a day later.
        var inside = await StoreAsync(type, "2026-10-17T01:00:00+08:00");
        var from = DateTimeOffset.Parse("2026-10-16T00:00:00Z", CultureInfo.InvariantCulture);

        var found = await ReadWindowAsync(type, "startsAt", from, from.AddDays(1));

        found.Should().Equal(inside);
    }

    private async Task<List<Guid>> ReadWindowAsync(string type, string field, DateTimeOffset from, DateTimeOffset to)
    {
        await using var session = factory.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var low = from.UtcDateTime.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var high = to.UtcDateTime.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var rows = await session.Query<Content>()
            .Where(c => c.ContentType == type)
            .Where(c => c.MatchesSql(PrefilterSql, field, low, field, high))
            .ToListAsync(Ct);

        return rows
            .Where(c => c.Data.TryGetValue(field, out var raw)
                        && DateTimeOffset.TryParse(raw?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
                        && value >= from && value < to)
            .Select(c => c.Id)
            .ToList();
    }

    private async Task<Guid> StoreAsync(string type, string? startsAt)
    {
        var data = new Dictionary<string, object>();
        if (startsAt is not null) data["startsAt"] = startsAt;

        var content = new Content { Id = Guid.CreateVersion7(), ContentType = type, Status = ContentStatus.Published, Data = data };
        await using var session = factory.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Store(content);
        await session.SaveChangesAsync(Ct);
        return content.Id;
    }
}
