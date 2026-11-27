using System.Net;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using barakoCMS.Events;
using barakoCMS.Infrastructure.Rollups;
using barakoCMS.Models;
using Probe = BarakoCMS.Tests.TokenFieldProbe;
using R = BarakoCMS.Tests.RollupProbe;

namespace BarakoCMS.Tests;

/// <summary>
/// What a reader is shown of a rollup: the value by the field's level, and the definition only
/// when the reader may read what it names.
/// </summary>
/// <remarks>
/// The types here are stored straight into the default tenant, so the level of the rollup, the
/// reference and the summed field is whatever each test needs.
/// </remarks>
[Collection("Integration1")]
public class RollupReadTests
{
    private readonly IntegrationTestFixture _fixture;

    public RollupReadTests(IntegrationTestFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => R.Ct;

    /// <summary>A pair whose rollups, reference and summed field are all Sensitive.</summary>
    private Task<R.Pair> SensitivePairAsync(bool deliverable = false) =>
        R.StorePairAsync(_fixture, SensitivityLevel.Sensitive, deliverable, SensitivityLevel.Sensitive, SensitivityLevel.Sensitive);

    private async Task<JsonElement> ShapeAsync(string token, string type)
    {
        var res = await R.Client(_fixture, token).GetAsync($"/api/contents/types/{type}", Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static JsonElement? Field(JsonElement shape, string name) =>
        shape.GetProperty("fields").EnumerateArray()
            .Where(f => f.GetProperty("name").GetString() == name)
            .Select(f => (JsonElement?)f)
            .FirstOrDefault();

    private static bool HasDefinition(JsonElement field) =>
        field.TryGetProperty("rollup", out var rollup) && rollup.ValueKind == JsonValueKind.Object;

    private async Task<(JsonElement Shape, string Line)> ShapeForAsync(
        string? capability, Func<R.Pair, (string Type, List<string>? Fields)[]> reads)
    {
        var pair = await SensitivePairAsync();
        var token = await R.ReaderAsync(_fixture, capability, reads(pair));
        return (await ShapeAsync(token, pair.Header), pair.Line);
    }

    [Fact]
    public async Task An_admin_is_shown_the_whole_definition()
    {
        var pair = await SensitivePairAsync();

        var shape = await ShapeAsync(await Probe.AdminAsync(_fixture), pair.Header);

        var total = Field(shape, "Total")!.Value.GetProperty("rollup");
        total.GetProperty("from").GetString().Should().Be(pair.Line);
        total.GetProperty("via").GetString().Should().Be("Teacher");
        total.GetProperty("fn").GetString().Should().Be("sum");
        total.GetProperty("field").GetString().Should().Be("Hours");
        HasDefinition(Field(shape, "Count")!.Value).Should().BeTrue();
    }

    [Fact]
    public async Task A_reader_of_the_header_the_rollup_and_both_fields_it_names_is_shown_it_whole()
    {
        var (shape, line) = await ShapeForAsync(
            SystemCapabilities.ViewSensitive, p => [(p.Header, null), (p.Line, null)]);

        Field(shape, "Total")!.Value.GetProperty("rollup").GetProperty("from").GetString().Should().Be(line);
        HasDefinition(Field(shape, "Count")!.Value).Should().BeTrue();
        HasDefinition(Field(shape, "Peak")!.Value).Should().BeTrue();
    }

    [Fact]
    public async Task A_reader_who_may_not_read_the_summed_field_is_shown_the_field_and_no_definition_that_names_it()
    {
        var (shape, _) = await ShapeForAsync(
            SystemCapabilities.ViewSensitive, p => [(p.Header, null), (p.Line, ["Teacher"])]);

        Field(shape, "Total").Should().NotBeNull("the reader may read the rollup field");
        HasDefinition(Field(shape, "Total")!.Value).Should().BeFalse("it names Hours");
        HasDefinition(Field(shape, "Peak")!.Value).Should().BeFalse("it names Hours");
        HasDefinition(Field(shape, "Count")!.Value).Should().BeTrue("a count names only the reference the reader may read");
    }

    [Fact]
    public async Task A_reader_who_may_not_read_the_via_field_is_shown_no_definition_on_any_rollup()
    {
        var (shape, _) = await ShapeForAsync(
            SystemCapabilities.ViewSensitive, p => [(p.Header, null), (p.Line, ["Hours"])]);

        foreach (var name in new[] { "Count", "Total", "Peak" })
        {
            Field(shape, name).Should().NotBeNull();
            HasDefinition(Field(shape, name)!.Value).Should().BeFalse($"{name} goes through Teacher");
        }
    }

    [Fact]
    public async Task A_reader_who_may_not_read_the_window_field_is_shown_no_definition()
    {
        var (header, line) = ("rh" + Guid.NewGuid().ToString("n")[..10], "rl" + Guid.NewGuid().ToString("n")[..10]);
        using (var scope = R.ScopeOf(_fixture))
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = line, DisplayName = line,
                Fields = [R.Reference("Teacher", header), R.Plain("Day", "date")],
            });
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = header, DisplayName = header,
                Fields =
                [
                    R.Plain("Name", "string"),
                    R.Rollup("Recent", "int", line, "Teacher", "count", windowKey: "Day", window: "last7Days"),
                    R.Rollup("Made", "int", line, "Teacher", "count", windowKey: "createdAt", window: "thisMonth"),
                ],
            });
            await session.SaveChangesAsync(Ct);
        }

        var token = await R.ReaderAsync(_fixture, null, (header, null), (line, ["Teacher"]));
        var shape = await ShapeAsync(token, header);

        Field(shape, "Recent").Should().NotBeNull();
        HasDefinition(Field(shape, "Recent")!.Value).Should().BeFalse("the reader may not read Day");
        HasDefinition(Field(shape, "Made")!.Value).Should().BeTrue("createdAt is not a field the reader needs to read");

        var whole = await ShapeAsync(await R.ReaderAsync(_fixture, null, (header, null), (line, null)), header);
        HasDefinition(Field(whole, "Recent")!.Value).Should().BeTrue();
    }

    [Fact]
    public async Task A_reader_who_may_not_read_the_summed_type_is_shown_the_field_and_no_definition()
    {
        var (shape, _) = await ShapeForAsync(SystemCapabilities.ViewSensitive, p => [(p.Header, null)]);

        Field(shape, "Count").Should().NotBeNull();
        foreach (var name in new[] { "Count", "Total", "Peak" })
            HasDefinition(Field(shape, name)!.Value).Should().BeFalse($"{name} names a type the reader may not read");
    }

    [Fact]
    public async Task A_reader_without_the_capability_is_not_shown_a_sensitive_rollup_at_all()
    {
        var (shape, _) = await ShapeForAsync(null, p => [(p.Header, null), (p.Line, null)]);

        Field(shape, "Name").Should().NotBeNull();
        foreach (var name in new[] { "Count", "Total", "Peak" })
            Field(shape, name).Should().BeNull($"{name} is Sensitive");
    }

    // The value, by the level of the field.

    private async Task<JsonElement> EntryDataAsync(string token, Guid id)
    {
        var res = await R.Client(_fixture, token).GetAsync($"/api/contents/{id}", Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var root = JsonDocument.Parse(body).RootElement;
        return (root.TryGetProperty("data", out var data) ? data : root.GetProperty("Data")).Clone();
    }

    private static decimal? Number(JsonElement data, string name) =>
        data.EnumerateObject()
            .Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Number)
            .Select(p => (decimal?)p.Value.GetDecimal())
            .FirstOrDefault();

    [Fact]
    public async Task A_sensitive_count_is_shown_to_the_capability_and_not_to_a_reader_without_it()
    {
        var pair = await SensitivePairAsync();
        var admin = await Probe.AdminAsync(_fixture);
        var header = await R.HeaderAsync(_fixture, admin, pair);
        await R.SeedLineAsync(_fixture, pair, header, 1);
        await R.SeedLineAsync(_fixture, pair, header, 100, level: SensitivityLevel.Sensitive);
        await R.DrainAsync(_fixture);
        var plain = await R.UserAsync(_fixture, null, pair.Header, pair.Line);
        var capable = await R.UserAsync(_fixture, SystemCapabilities.ViewSensitive, pair.Header, pair.Line);

        var without = await EntryDataAsync(plain, header);
        var with = await EntryDataAsync(capable, header);

        Number(without, "Count").Should().BeNull();
        Number(without, "Total").Should().BeNull();
        Number(with, "Count").Should().Be(2m);
        Number(with, "Total").Should().Be(101m);
    }

    [Fact]
    public async Task Anonymous_delivery_serves_a_public_rollup_and_withholds_a_sensitive_one()
    {
        var admin = await Probe.AdminAsync(_fixture);
        var open = await R.StorePairAsync(_fixture, SensitivityLevel.Public, deliverable: true);
        var closed = await R.StorePairAsync(_fixture, SensitivityLevel.Sensitive, deliverable: true, SensitivityLevel.Sensitive, SensitivityLevel.Sensitive);
        var openHeader = await R.HeaderAsync(_fixture, admin, open);
        var closedHeader = await R.HeaderAsync(_fixture, admin, closed);
        await R.SeedLineAsync(_fixture, open, openHeader, 5);
        await R.SeedLineAsync(_fixture, closed, closedHeader, 5);
        await R.DrainAsync(_fixture);

        (await R.PublicNumberAsync(_fixture, open.Header, openHeader, "Count")).Should().Be(1m);
        (await R.PublicNumberAsync(_fixture, open.Header, openHeader, "Total")).Should().Be(5m);
        (await R.PublicNumberAsync(_fixture, closed.Header, closedHeader, "Count")).Should().BeNull();
        (await R.PublicNumberAsync(_fixture, closed.Header, closedHeader, "Total")).Should().BeNull();
    }

    [Fact]
    public async Task Delivery_serves_the_stored_value_and_does_not_count_on_read()
    {
        var admin = await Probe.AdminAsync(_fixture);
        var pair = await R.StorePairAsync(_fixture, SensitivityLevel.Public, deliverable: true);
        var header = await R.HeaderAsync(_fixture, admin, pair);
        await R.SeedLineAsync(_fixture, pair, header, 5);
        await R.DrainAsync(_fixture);
        await StoreCountAsync(header, 77m);

        (await R.PublicNumberAsync(_fixture, pair.Header, header, "Count")).Should().Be(77m);
    }

    private async Task<JsonElement> EntryAsync(string token, Guid id)
    {
        var res = await R.Client(_fixture, token).GetAsync($"/api/contents/{id}", Ct);
        var body = await res.Content.ReadAsStringAsync(Ct);
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task Rollups_at_is_null_before_the_first_recount_and_set_after_it()
    {
        var admin = await Probe.AdminAsync(_fixture);
        var pair = await R.StorePairAsync(_fixture, SensitivityLevel.Public);
        var header = await R.StoreUncountedHeaderAsync(_fixture, pair);

        var before = await EntryAsync(admin, header);
        before.GetProperty("rollupsAt").ValueKind.Should().Be(JsonValueKind.Null);
        (await R.StoredAsync(_fixture, header)).RollupsAt.Should().BeNull();

        await R.SeedLineAsync(_fixture, pair, header, 4);
        var started = DateTime.UtcNow.AddMinutes(-1);
        await R.DrainAsync(_fixture);

        var after = await EntryAsync(admin, header);
        after.GetProperty("rollupsAt").GetDateTimeOffset().UtcDateTime.Should().BeAfter(started);
        (await R.StoredAsync(_fixture, header)).RollupsAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_recount_that_finds_the_same_values_leaves_rollups_at_alone()
    {
        var admin = await Probe.AdminAsync(_fixture);
        var pair = await R.StorePairAsync(_fixture, SensitivityLevel.Public);
        var header = await R.HeaderAsync(_fixture, admin, pair);
        await R.SeedLineAsync(_fixture, pair, header, 4);
        await R.DrainAsync(_fixture);
        var first = (await R.StoredAsync(_fixture, header)).RollupsAt;
        first.Should().NotBeNull();

        using (var scope = R.ScopeOf(_fixture))
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new RollupMark { Id = header, HeaderType = pair.Header, MarkedAt = DateTimeOffset.UtcNow });
            await session.SaveChangesAsync(Ct);
        }

        await R.DrainAsync(_fixture);

        (await R.StoredAsync(_fixture, header)).RollupsAt.Should().Be(first);
    }

    [Fact]
    public void An_event_with_no_rollups_at_leaves_the_entry_s_alone()
    {
        var stamp = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
        var entry = new Content { Id = Guid.NewGuid(), RollupsAt = stamp };
        var plain = new ContentFieldChanged(entry.Id, "Count", new() { ["Count"] = 2 }, null, Guid.Empty, stamp.AddDays(5));
        var counted = plain with { RollupsAt = stamp.AddDays(5) };

        entry.Apply(plain, plain.OccurredAt);
        entry.RollupsAt.Should().Be(stamp);

        entry.Apply(counted, counted.OccurredAt);
        entry.RollupsAt.Should().Be(stamp.AddDays(5));
    }

    private async Task StoreCountAsync(Guid id, decimal value)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var entry = (await session.LoadAsync<Content>(id, Ct))!;
        entry.Data["Count"] = value;
        session.Store(entry);
        await session.SaveChangesAsync(Ct);
    }
}
