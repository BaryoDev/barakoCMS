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
        var (pair, _, _) = await StartAsync();
        var otherType = "ro" + Guid.NewGuid().ToString("n")[..10];
        var stranger = Guid.NewGuid();
        using (var scope = _fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new Content
            {
                Id = stranger, ContentType = otherType, Data = new() { ["Name"] = "kept" }, Status = ContentStatus.Published,
            });
            await session.SaveChangesAsync(Ct);
        }

        var before = await DocumentAsync(stranger);
        await R.SeedLineAsync(_fixture, pair, stranger, 4);
        (await R.MarkedAsync(_fixture)).Should().Contain(stranger, "the write marked what the reference names");

        await R.DrainAsync(_fixture);

        var after = await DocumentAsync(stranger);
        after.Json.Should().Be(before.Json, "the drain writes headers of its own type only");
        after.Version.Should().Be(before.Version);
        (await R.MarkedAsync(_fixture)).Should().NotContain(stranger, "and the mark is not left to be retried for ever");
    }

    [Fact]
    public async Task A_user_edit_that_commits_before_the_drain_does_is_kept_and_the_next_drain_recounts()
    {
        var (pair, admin, header) = await StartAsync();
        await R.LineAsync(_fixture, admin, pair, header, 4);
        var edit = new EditBeforeCommit(async () =>
        {
            var res = await R.Client(_fixture, admin).PutAsJsonAsync(
                $"/api/contents/{header}", new { id = header, data = new Dictionary<string, object> { ["Name"] = "edited" } }, Ct);
            res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync(Ct));
        });

        var run = async () => await RollupDrain.RunAsync(
            _fixture.Services.GetRequiredService<IDocumentStore>(), _fixture.Services.GetRequiredService<IConfiguration>(),
            R.Tenant, Ct, edit);

        (await run.Should().ThrowAsync<Exception>()).Which.Should().Match<Exception>(e => TestHelpers.LostConcurrencyCheck(e));
        var held = await R.StoredAsync(_fixture, header);
        Convert.ToString(held.Data["Name"]).Should().Be("edited", "the drain lost and wrote nothing over the edit");
        R.Num(held, "Count").Should().NotBe(1);
        (await R.MarkedAsync(_fixture)).Should().Contain(header, "the mark stays, so the retry has work");

        await R.DrainAsync(_fixture);

        (await R.ValuesAsync(_fixture, header)).Should().Be((1m, 4m, 4m));
        Convert.ToString((await R.StoredAsync(_fixture, header)).Data["Name"]).Should().Be("edited");
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

    private async Task<(string Json, Guid Version)> DocumentAsync(Guid id)
    {
        using var scope = _fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IQuerySession>();
        var content = (await session.LoadAsync<Content>(id, Ct))!;
        return (JsonSerializer.Serialize(content.Data), (await session.MetadataForAsync(content, Ct))!.CurrentVersion);
    }

    private async Task<Guid> VersionAsync(Guid id) => (await DocumentAsync(id)).Version;
}
