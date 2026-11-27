using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Rollups;
using barakoCMS.Infrastructure.Services;
using Probe = BarakoCMS.Tests.TokenFieldProbe;
using R = BarakoCMS.Tests.RollupProbe;
using barakoCMS.Models;

namespace BarakoCMS.Tests;

/// <summary>
/// A write to an entry of the summarised type marks its header in the same transaction, and the
/// drain recomputes the header from the entries that count. The tests run the drain themselves, so
/// they never wait on the queue.
/// </summary>
[Collection("Integration1")]
public class RollupWriteTests
{
    private readonly IntegrationTestFixture _fixture;

    public RollupWriteTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        FastEndpointsResolver.ClaimJobQueues(fixture.Services);
    }

    private static CancellationToken Ct => R.Ct;

    private async Task<(R.Pair Pair, string Admin, Guid Header)> StartAsync(SensitivityLevel level = SensitivityLevel.Public)
    {
        var pair = await R.StorePairAsync(_fixture, level);
        var admin = await Probe.AdminAsync(_fixture);
        var header = await R.HeaderAsync(_fixture, admin, pair);
        return (pair, admin, header);
    }

    [Fact]
    public async Task A_header_with_no_lines_counts_and_sums_to_zero_and_has_no_peak()
    {
        var (_, _, header) = await StartAsync();

        await R.DrainAsync(_fixture);

        var entry = await R.StoredAsync(_fixture, header);
        R.Num(entry, "Count").Should().Be(0);
        R.Num(entry, "Total").Should().Be(0);
        R.Num(entry, "Peak").Should().BeNull("there is no largest of nothing");
    }

    [Fact]
    public async Task Creating_a_line_counts_it_for_its_header()
    {
        var (pair, admin, header) = await StartAsync();

        await R.LineAsync(_fixture, admin, pair, header, 4);
        await R.LineAsync(_fixture, admin, pair, header, 6);
        await R.DrainAsync(_fixture);

        (await R.ValuesAsync(_fixture, header)).Should().Be((2m, 10m, 6m));
        (await R.MarkedAsync(_fixture)).Should().NotContain(header, "the drain deletes the mark it took");
    }

    [Fact]
    public async Task Editing_a_line_changes_the_header_it_counts_for()
    {
        var (pair, admin, header) = await StartAsync();
        var line = await R.LineAsync(_fixture, admin, pair, header, 4);
        await R.DrainAsync(_fixture);

        var res = await Probe.UpdateAsync(_fixture, admin, line, new() { ["Teacher"] = header.ToString(), ["Hours"] = 9 });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        await R.DrainAsync(_fixture);

        (await R.ValuesAsync(_fixture, header)).Should().Be((1m, 9m, 9m));
    }

    [Fact]
    public async Task Moving_a_line_to_another_header_updates_both()
    {
        var (pair, admin, first) = await StartAsync();
        var second = await R.HeaderAsync(_fixture, admin, pair);
        var line = await R.LineAsync(_fixture, admin, pair, first, 4);
        await R.DrainAsync(_fixture);

        var res = await Probe.UpdateAsync(_fixture, admin, line, new() { ["Teacher"] = second.ToString(), ["Hours"] = 4 });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        await R.DrainAsync(_fixture);

        (await R.ValuesAsync(_fixture, first)).Count.Should().Be(0, "the line left");
        (await R.ValuesAsync(_fixture, second)).Count.Should().Be(1, "the line arrived");
    }

    [Fact]
    public async Task Archiving_a_line_and_bringing_it_back_follows_the_count()
    {
        var (pair, admin, header) = await StartAsync();
        var line = await R.LineAsync(_fixture, admin, pair, header, 4);
        await R.DrainAsync(_fixture);
        var client = R.Client(_fixture, admin);

        (await client.PutAsJsonAsync($"/api/contents/{line}/status", new { newStatus = "Archived" }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        await R.DrainAsync(_fixture);
        (await R.ValuesAsync(_fixture, header)).Count.Should().Be(0);

        var back = await client.PutAsJsonAsync($"/api/contents/{line}/status", new { newStatus = "Published" }, Ct);
        back.StatusCode.Should().Be(HttpStatusCode.OK, await back.Content.ReadAsStringAsync(Ct));
        await R.DrainAsync(_fixture);
        (await R.ValuesAsync(_fixture, header)).Count.Should().Be(1);
    }

    [Fact]
    public async Task Erasing_a_line_takes_it_out_of_the_count()
    {
        var (pair, admin, header) = await StartAsync();
        var line = await R.LineAsync(_fixture, admin, pair, header, 4);
        await R.LineAsync(_fixture, admin, pair, header, 5);
        await R.DrainAsync(_fixture);
        (await R.ValuesAsync(_fixture, header)).Count.Should().Be(2);

        var erased = await R.Client(_fixture, admin).DeleteAsync($"/api/contents/{line}/erase", Ct);
        erased.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
        await R.DrainAsync(_fixture);

        (await R.ValuesAsync(_fixture, header)).Should().Be((1m, 5m, 5m));
    }

    [Fact]
    public async Task Twenty_lines_written_at_once_sum_exactly()
    {
        var (pair, admin, header) = await StartAsync();

        await Task.WhenAll(Enumerable.Range(1, 20).Select(i => R.LineAsync(_fixture, admin, pair, header, i)));
        await R.DrainAsync(_fixture);

        (await R.ValuesAsync(_fixture, header)).Should().Be((20m, 210m, 20m));
    }

    [Fact]
    public async Task A_public_rollup_counts_published_entries_of_public_level_only()
    {
        var (pair, admin, header) = await StartAsync();
        var other = await R.HeaderAsync(_fixture, admin, pair);
        await R.SeedLineAsync(_fixture, pair, header, 1);
        await R.SeedLineAsync(_fixture, pair, header, 10, ContentStatus.Draft);
        await R.SeedLineAsync(_fixture, pair, header, 100, ContentStatus.Archived);
        await R.SeedLineAsync(_fixture, pair, header, 1000, level: SensitivityLevel.Hidden);
        await R.SeedLineAsync(_fixture, pair, header, 10000, level: SensitivityLevel.Sensitive);
        await R.SeedLineAsync(_fixture, pair, other, 100000);

        await R.DrainAsync(_fixture);

        (await R.ValuesAsync(_fixture, header)).Should().Be((1m, 1m, 1m));
        (await R.ValuesAsync(_fixture, other)).Should().Be((1m, 100000m, 100000m), "a line counts for the header it names");
    }

    [Fact]
    public async Task A_sensitive_rollup_also_counts_drafts_and_sensitive_entries_but_not_hidden_or_archived_ones()
    {
        var (pair, _, header) = await StartAsync(SensitivityLevel.Sensitive);
        await R.SeedLineAsync(_fixture, pair, header, 1);
        await R.SeedLineAsync(_fixture, pair, header, 10, ContentStatus.Draft);
        await R.SeedLineAsync(_fixture, pair, header, 100, level: SensitivityLevel.Sensitive);
        await R.SeedLineAsync(_fixture, pair, header, 1000, level: SensitivityLevel.Hidden);
        await R.SeedLineAsync(_fixture, pair, header, 10000, ContentStatus.Archived);

        await R.DrainAsync(_fixture);

        (await R.ValuesAsync(_fixture, header)).Should().Be((3m, 111m, 100m));
    }

    [Fact]
    public async Task The_drain_writes_a_field_changed_event_and_no_update()
    {
        var (pair, admin, header) = await StartAsync();
        await R.LineAsync(_fixture, admin, pair, header, 4);

        await R.DrainAsync(_fixture);

        var history = await R.Client(_fixture, admin).GetFromJsonAsync<JsonElement>($"/api/contents/{header}/history", Ct);
        var kinds = history.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("changeType").GetString()).ToList();
        kinds.Should().Contain("FieldChanged");
        kinds.Should().NotContain("Updated", "a recount is not an edit, and starts no workflow");
    }

    [Fact]
    public async Task A_repeated_drain_leaves_the_values_and_the_version_alone()
    {
        var (pair, admin, header) = await StartAsync();
        await R.LineAsync(_fixture, admin, pair, header, 4);
        await R.DrainAsync(_fixture);
        var before = await VersionAsync(header);

        await R.DrainAsync(_fixture);

        (await VersionAsync(header)).Should().Be(before);
        (await R.ValuesAsync(_fixture, header)).Count.Should().Be(1);
    }

    [Fact]
    public async Task A_client_cannot_send_a_rollup_value_and_leaving_it_out_keeps_the_stored_one()
    {
        var (pair, admin, header) = await StartAsync();
        await R.LineAsync(_fixture, admin, pair, header, 4);
        await R.DrainAsync(_fixture);

        var created = await R.Client(_fixture, admin).PostAsJsonAsync(
            "/api/contents",
            new barakoCMS.Features.Content.Create.Request
            {
                ContentType = pair.Header, Data = new() { ["Name"] = "x", ["Count"] = 99 }, Status = ContentStatus.Published,
            },
            ApiJson.Options, Ct);
        created.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await created.Content.ReadAsStringAsync(Ct)).Should().Contain("Count");

        var sent = await Probe.UpdateAsync(_fixture, admin, header, new() { ["Name"] = "y", ["Count"] = 99 });
        sent.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var left = await Probe.UpdateAsync(_fixture, admin, header, new() { ["Name"] = "z" });
        left.StatusCode.Should().Be(HttpStatusCode.OK, await left.Content.ReadAsStringAsync(Ct));
        var stored = await R.StoredAsync(_fixture, header);
        stored.Data["Name"].ToString().Should().Be("z");
        R.Num(stored, "Count").Should().Be(1, "the edit left the stored count in place");
    }

    [Fact]
    public async Task A_client_may_send_the_stored_rollup_value_back()
    {
        var (pair, admin, header) = await StartAsync();
        await R.LineAsync(_fixture, admin, pair, header, 4);
        await R.DrainAsync(_fixture);

        var res = await Probe.UpdateAsync(_fixture, admin, header, new() { ["Name"] = "w", ["Count"] = 1, ["Total"] = 4, ["Peak"] = 4 });

        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
    }

    private async Task<WorkflowActionResult> SetFieldAsync(Guid id, string field, string value)
    {
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession(R.Tenant);
        var writer = new barakoCMS.Infrastructure.Services.ContentWriter(
            session, new barakoCMS.Infrastructure.Services.ContentSourcingPolicyService(session), new ConfigurationBuilder().Build());
        var action = new UpdateFieldAction(
            session, writer, new ContentLifecycleRunner([], session),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UpdateFieldAction>.Instance);

        return await action.RunAsync(new() { ["Field"] = field, ["Value"] = value }, new Content { Id = id, LastModifiedBy = Guid.NewGuid() }, Ct);
    }

    [Fact]
    public async Task A_workflow_field_write_cannot_set_a_rollup()
    {
        var (_, _, header) = await StartAsync();
        await R.DrainAsync(_fixture);

        var result = await SetFieldAsync(header, "data.Count", "5");

        result.Succeeded.Should().BeFalse("the server owns the value");
        R.Num(await R.StoredAsync(_fixture, header), "Count").Should().Be(0);
    }

    [Fact]
    public async Task A_workflow_field_write_to_a_line_with_no_stream_marks_its_header()
    {
        var (pair, _, header) = await StartAsync();
        var line = Guid.NewGuid();
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Content
            {
                Id = line, ContentType = pair.Line, Status = ContentStatus.Published,
                Data = new() { ["Teacher"] = header.ToString(), ["Hours"] = 1 },
            });
            await session.SaveChangesAsync(Ct);
        }

        var result = await SetFieldAsync(line, "data.Hours", "8");
        result.Succeeded.Should().BeTrue(result.Error);
        await R.DrainAsync(_fixture);

        // The action stores the text '8'. A number stored as text is skipped, as the aggregate query
        // skips it, so the line counts and adds nothing. The count is what shows the header was marked.
        (await R.ValuesAsync(_fixture, header)).Should().Be((1m, 0m, null));
    }

    [Fact]
    public async Task Restoring_an_old_version_of_a_header_keeps_the_current_rollup_values()
    {
        var (pair, admin, header) = await StartAsync();
        await R.LineAsync(_fixture, admin, pair, header, 4);
        await R.DrainAsync(_fixture);
        var client = R.Client(_fixture, admin);
        var history = await client.GetFromJsonAsync<JsonElement>($"/api/contents/{header}/history", Ct);
        var first = history.GetProperty("items")[0].GetProperty("versionId").GetGuid();

        var restored = await client.PostAsync($"/api/contents/{header}/rollback/{first}", null, Ct);

        restored.StatusCode.Should().Be(HttpStatusCode.OK, await restored.Content.ReadAsStringAsync(Ct));
        (await R.ValuesAsync(_fixture, header)).Should().Be((1m, 4m, 4m), "the version from before the line holds no count");
    }

    [Fact]
    public async Task The_drain_leaves_an_entry_of_another_type_alone_when_a_reference_names_it()
    {
        var (tenant, _) = await R.OwnTenantAsync(_fixture);
        var pair = await R.StorePairAsync(_fixture, tenant: tenant);
        var otherType = "ro" + Guid.NewGuid().ToString("n")[..10];
        var stranger = Guid.NewGuid();
        using (var scope = R.ScopeOf(_fixture, tenant))
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Content
            {
                Id = stranger, ContentType = otherType, Data = new() { ["Name"] = "kept" }, Status = ContentStatus.Published,
            });
            await session.SaveChangesAsync(Ct);
        }

        var before = await DocumentAsync(stranger, tenant);
        await R.SeedLineAsync(_fixture, pair, stranger, 4, tenant: tenant);
        await R.DropQueuedDrainsAsync(_fixture, tenant);
        (await R.MarkedAsync(_fixture, tenant)).Should().Contain(stranger, "the write marked what the reference names");

        await R.DrainAsync(_fixture, tenant);

        var after = await DocumentAsync(stranger, tenant);
        after.Json.Should().Be(before.Json, "the drain writes headers of its own type only");
        after.Version.Should().Be(before.Version);
        (await R.MarkedAsync(_fixture, tenant)).Should().NotContain(stranger, "and the mark is not left to be retried for ever");
    }

    [Fact]
    public async Task A_user_edit_that_commits_before_the_drain_does_is_kept_and_the_next_drain_recounts()
    {
        // A tenant of its own, so no other test's drain job takes this test's marks.
        var (tenant, client) = await R.OwnTenantAsync(_fixture);
        var pair = await R.StorePairAsync(_fixture, tenant: tenant);
        var header = await R.CreateAsync(client, pair.Header, new() { ["Name"] = "h" });
        await R.CreateAsync(client, pair.Line, new() { ["Teacher"] = header.ToString(), ["Hours"] = 4m });
        await R.DropQueuedDrainsAsync(_fixture, tenant);

        // Stored in the edit's step, after the drain read its marks and the types it skips. From
        // there the queue's own drain skips the type too, so it cannot recount between the checks.
        var hold = new FieldChange
        {
            Id = Guid.NewGuid(), ContentType = pair.Header, Field = "Count", Kind = FieldChangeKind.Recompute,
            State = FieldChangeState.Running, Total = 1, StartedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var edit = new EditBeforeCommit(async () =>
        {
            var res = await client.PutAsJsonAsync(
                $"/api/contents/{header}", new { id = header, data = new Dictionary<string, object> { ["Name"] = "edited" } }, Ct);
            res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
            using var scope = R.ScopeOf(_fixture, tenant);
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(hold);
            await session.SaveChangesAsync(Ct);
        });

        var run = async () => await RollupDrain.RunAsync(
            _fixture.Services.GetRequiredService<IDocumentStore>(), _fixture.Services.GetRequiredService<IConfiguration>(),
            tenant, Ct, edit);

        try
        {
            (await run.Should().ThrowAsync<Exception>()).Which.Should().Match<Exception>(e => TestHelpers.LostConcurrencyCheck(e));
            var held = await R.StoredAsync(_fixture, header, tenant);
            Convert.ToString(held.Data["Name"]).Should().Be("edited", "the drain lost and wrote nothing over the edit");
            R.Num(held, "Count").Should().NotBe(1);
            (await R.MarkedAsync(_fixture, tenant)).Should().Contain(header, "the mark stays, so the retry has work");
        }
        finally
        {
            await ReleaseAsync(hold.Id, tenant);
        }

        await R.DrainAsync(_fixture, tenant);

        (await R.ValuesAsync(_fixture, header, tenant)).Should().Be((1m, 4m, 4m));
        Convert.ToString((await R.StoredAsync(_fixture, header, tenant)).Data["Name"]).Should().Be("edited");
    }

    private async Task ReleaseAsync(Guid id, string tenant)
    {
        using var scope = R.ScopeOf(_fixture, tenant);
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Delete<FieldChange>(id);
        await session.SaveChangesAsync(Ct);
    }

    private sealed class EditBeforeCommit(Func<Task> edit) : DocumentSessionListenerBase
    {
        private int _ran;

        public override async Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
        {
            if (Interlocked.Exchange(ref _ran, 1) == 0)
            {
                await edit();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_line_moved_between_two_headers_marks_them_in_id_order_whichever_way_it_moves(bool reverse)
    {
        var pair = await R.StorePairAsync(_fixture);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() }.OrderBy(i => i).ToArray();
        var (from, to) = reverse ? (ids[1], ids[0]) : (ids[0], ids[1]);
        var line = new Content
        {
            Id = Guid.NewGuid(), ContentType = pair.Line, Status = ContentStatus.Published,
            Data = new() { ["Teacher"] = to.ToString(), ["Hours"] = 4m },
        };
        var before = EntryFacts.Of(new Content
        {
            Id = line.Id, ContentType = pair.Line, Status = ContentStatus.Published,
            Data = new() { ["Teacher"] = from.ToString(), ["Hours"] = 4m },
        });

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        await RollupMarks.NoteAsync(session, before, line, Ct);
        var probe = new MarkOrderProbe();
        session.Listeners.Add(probe);
        await session.SaveChangesAsync(Ct);

        probe.Seen.Should().Equal(ids, "the mark rows are locked in one order, so opposite moves cannot wait on each other");
    }

    [Fact]
    public async Task Marks_noted_in_two_calls_of_one_transaction_are_stored_once_in_id_order()
    {
        var pair = await R.StorePairAsync(_fixture);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() }.OrderBy(i => i).ToArray();
        Content LineFor(Guid header) => new()
        {
            Id = Guid.NewGuid(), ContentType = pair.Line, Status = ContentStatus.Published,
            Data = new() { ["Teacher"] = header.ToString(), ["Hours"] = 4m },
        };

        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        await RollupMarks.NoteAsync(session, null, LineFor(ids[1]), Ct);
        var probe = new MarkOrderProbe();
        session.Listeners.Add(probe);
        await RollupMarks.NoteAsync(session, null, LineFor(ids[0]), Ct);
        await session.SaveChangesAsync(Ct);

        probe.Seen.Should().Equal(ids, "a later call does not store its header after an earlier call's");
    }

    /// <summary>Reads the mark writes pending when the commit starts, after the listener that stores them.</summary>
    private sealed class MarkOrderProbe : DocumentSessionListenerBase
    {
        public List<Guid> Seen { get; } = [];

        public override Task BeforeSaveChangesAsync(IDocumentSession session, CancellationToken token)
        {
            Seen.AddRange(session.PendingChanges.Updates().OfType<RollupMark>().Select(m => m.Id));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_null_an_array_and_text_in_the_summed_field_count_as_entries_and_add_nothing()
    {
        var (pair, admin, header) = await StartAsync();
        await R.LineAsync(_fixture, admin, pair, header, 4);
        foreach (var kind in new object?[] { null, new object[] { 3, 4 }, "abc" })
        {
            await R.SeedAsync(_fixture, pair.Line, new() { ["Teacher"] = header.ToString(), ["Hours"] = kind! });
        }

        await R.DrainAsync(_fixture);

        (await R.ValuesAsync(_fixture, header)).Should().Be((4m, 4m, 4m), "all four count, and only the number is summed");
    }

    private const long Huge = 6_000_000_000_000_000_000;

    /// <summary>
    /// A header whose int sum two lines carry past the range of a long, in a tenant of its own: the
    /// header holds a mark the drain skips, and no other test should meet it.
    /// </summary>
    private async Task<(R.Pair Pair, Guid Header, FieldDefinition Rollup, FieldDefinition Source, string Tenant)> OverflowingAsync()
    {
        var (tenant, client) = await R.OwnTenantAsync(_fixture);
        var header = "rh" + Guid.NewGuid().ToString("n")[..10];
        var line = "rl" + Guid.NewGuid().ToString("n")[..10];
        var source = R.Plain("Qty", "int");
        var rollup = R.Rollup("Total", "int", line, "Teacher", "sum", "Qty");
        using (var scope = R.ScopeOf(_fixture, tenant))
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = line, DisplayName = line, Fields = [R.Reference("Teacher", header), source],
            });
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(), Name = header, DisplayName = header, Fields = [R.Plain("Name", "string"), rollup],
            });
            await session.SaveChangesAsync(Ct);
        }

        var pair = new R.Pair(header, line);
        var id = await R.CreateAsync(client, header, new() { ["Name"] = "h" });
        await R.SeedManyAsync(_fixture, line, Enumerable.Range(0, 2).Select(_ =>
            new Dictionary<string, object> { ["Teacher"] = id.ToString(), ["Qty"] = Huge }), tenant);
        await R.DropQueuedDrainsAsync(_fixture, tenant);

        return (pair, id, rollup, source, tenant);
    }

    [Fact]
    public async Task A_sum_past_the_range_of_an_int_field_leaves_the_header_and_clears_its_mark()
    {
        var (_, header, _, _, tenant) = await OverflowingAsync();
        (await R.MarkedAsync(_fixture, tenant)).Should().Contain(header);

        await R.DrainAsync(_fixture, tenant);

        R.Num(await R.StoredAsync(_fixture, header, tenant), "Total").Should().BeNull("a value that does not fit is not written");
        (await R.MarkedAsync(_fixture, tenant)).Should().NotContain(header, "the mark goes, or the drain would meet the same header for ever");
    }

    [Fact]
    public async Task A_skipped_header_is_named_once_in_a_warning_with_no_value()
    {
        var (pair, header, rollup, source, tenant) = await OverflowingAsync();
        var log = new ListLogger();

        using var scope = R.ScopeOf(_fixture, tenant);
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var values = await RollupAggregate.ValuesAsync(session, pair.Header, rollup, source, [header], Ct, null, log);

        values.Should().NotContainKey(header);
        var line = log.Lines.Should().ContainSingle().Which;
        line.Should().Contain(pair.Header).And.Contain("Total").And.Contain(header.ToString());
        line.Should().NotContain(Huge.ToString(), "the log says where to look and not what the numbers are");
    }

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }

    private async Task<(string Json, Guid Version)> DocumentAsync(Guid id, string tenant = R.Tenant)
    {
        using var scope = R.ScopeOf(_fixture, tenant);
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var content = (await session.LoadAsync<Content>(id, Ct))!;
        return (JsonSerializer.Serialize(content.Data), (await session.MetadataForAsync(content, Ct))!.CurrentVersion);
    }

    private async Task<Guid> VersionAsync(Guid id) => (await DocumentAsync(id)).Version;
}
