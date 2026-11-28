using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Models;
using BarakoCMS.Tests.Features.Tenants;
using Xunit;

namespace BarakoCMS.Tests.Features.ContentTypes;

/// <summary>
/// A recompute skips an entry it cannot rewrite, keeps its value, records its id and finishes the
/// rest. The skipped entries are retried as a new change.
/// </summary>
/// <remarks>
/// No stored value makes a formula or the writer throw, and a valid uniqueness rule cannot name a
/// computed field, so an entry is made to fail through the job's test hook, which throws for the
/// ids a test lists. The change rows are stored straight into the tenant and run here, so the job
/// queue never races a test for a batch.
/// </remarks>
[Collection("Integration1")]
public class RecomputeSkipsTests : IDisposable
{
    /// <summary>In every entry's Name and in every failure's message, so a leak shows.</summary>
    private const string Secret = "zq-secret-value-7431";

    private readonly IntegrationTestFixture _fixture;
    private readonly HashSet<Guid> _failing = [];

    public RecomputeSkipsTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        FastEndpointsResolver.ClaimJobQueues(fixture.Services);
        FieldChanges.BeforeEntryForTests = (content, _) => _failing.Contains(content.Id)
            ? throw new InvalidOperationException($"cannot rewrite {content.Data["Name"]}")
            : Task.CompletedTask;
    }

    public void Dispose() => FieldChanges.BeforeEntryForTests = null;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewName() => "skips" + Guid.NewGuid().ToString("n")[..10];

    private IDocumentStore Store => _fixture.Services.GetRequiredService<IDocumentStore>();

    private sealed record Seeded(string Slug, string Type, Guid[] Ids);

    private static FieldDefinition Plain(string name) => new() { Name = name, DisplayName = name, Type = "string" };

    private static FieldDefinition Formula(string name, string expression) => new()
    {
        Name = name, DisplayName = name, Type = "string", Computed = new ComputedDefinition { Expression = expression },
    };

    /// <summary>A tenant with a type holding <paramref name="count"/> entries whose Label is stale, in id order.</summary>
    private async Task<Seeded> SeedAsync(int count)
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        var type = NewName();
        var prefix = Guid.NewGuid().ToString("n")[..8];
        var ids = Enumerable.Range(1, count).Select(n => new Guid($"{prefix}-0000-0000-0000-{n:D12}")).ToArray();

        await using var session = Store.LightweightSession(slug);
        session.Store(new ContentTypeDefinition
        {
            Id = Guid.NewGuid(), Name = type, DisplayName = type,
            Fields = [Plain("Name"), Formula("Label", "upper(Name)")],
        });
        foreach (var id in ids)
        {
            session.Store(new Content
            {
                Id = id, ContentType = type, Status = ContentStatus.Published,
                Data = new Dictionary<string, object> { ["Name"] = $"{Secret} {id.ToString()[^2..]}", ["Label"] = "stale" },
            });
        }

        await session.SaveChangesAsync(Ct);
        return new Seeded(slug, type, ids);
    }

    private async Task<Guid> StoreChangeAsync(string slug, string type, Action<FieldChange>? shape = null, int total = 0)
    {
        var change = new FieldChange
        {
            Id = Guid.NewGuid(), ContentType = type, Field = "Label", Kind = FieldChangeKind.Recompute,
            State = FieldChangeState.Running, Total = total,
            StartedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        shape?.Invoke(change);
        await using var session = Store.LightweightSession(slug);
        session.Store(change);
        await session.SaveChangesAsync(Ct);
        return change.Id;
    }

    private Task<Guid> StoreRunningAsync(Seeded seeded) => StoreChangeAsync(seeded.Slug, seeded.Type, total: seeded.Ids.Length);

    /// <summary>Runs the change until it is no longer running, as the job does, in batches of <paramref name="batchSize"/>.</summary>
    private async Task<FieldChange> DriveAsync(string slug, Guid id, int batchSize = FieldChanges.BatchSize)
    {
        var configuration = _fixture.Services.GetRequiredService<IConfiguration>();
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                await FieldChanges.RunAsync(Store, configuration, slug, id, maxBatches: null, Ct, batchSize);
            }
            catch (Exception e) when (TestHelpers.LostConcurrencyCheck(e))
            {
                // A queued job committed the same batch first; the next run reads its progress.
            }

            var stored = await StoredAsync(slug, id);
            if (stored.State != FieldChangeState.Running)
            {
                return stored;
            }
        }

        throw new Xunit.Sdk.XunitException($"Field change {id} did not finish.");
    }

    private async Task<FieldChange> StoredAsync(string slug, Guid id)
    {
        await using var session = Store.QuerySession(slug);
        return (await session.LoadAsync<FieldChange>(id, Ct))!;
    }

    private async Task<Dictionary<Guid, Content>> EntriesAsync(Seeded seeded)
    {
        await using var session = Store.QuerySession(seeded.Slug);
        return (await session.Query<Content>().Where(c => c.ContentType == seeded.Type).ToListAsync(Ct)).ToDictionary(c => c.Id);
    }

    private async Task<int> EventCountAsync(string slug, Guid entryId)
    {
        await using var session = Store.QuerySession(slug);
        return (await session.Events.FetchStreamAsync(entryId, token: Ct)).Count;
    }

    private async Task<List<AuditEvent>> AuditAsync(string slug, string action, Guid changeId)
    {
        var target = changeId.ToString();
        await using var session = Store.QuerySession(slug);
        return (await session.Query<AuditEvent>().Where(e => e.Action == action && e.TargetId == target).ToListAsync(Ct)).ToList();
    }

    private static string Label(Content entry) => entry.Data["Label"]?.ToString() ?? string.Empty;

    private async Task<(string Slug, HttpClient Admin)> TenantAsync()
    {
        var slug = await TenantProfileSeed.TenantAsync(_fixture, Ct);
        return (slug, await TenantProfileSeed.AdminInAsync(_fixture, slug, Ct));
    }

    /// <summary>Seeds entries in a tenant that has an admin, and a change that completed with the given skips.</summary>
    private async Task<(Seeded Seeded, HttpClient Admin, Guid Change)> SkippedAsync(int entries, params int[] failing)
    {
        var seeded = await SeedAsync(entries);
        var admin = await TenantProfileSeed.AdminInAsync(_fixture, seeded.Slug, Ct);
        foreach (var n in failing)
        {
            _failing.Add(seeded.Ids[n]);
        }

        var id = await StoreRunningAsync(seeded);
        (await DriveAsync(seeded.Slug, id)).State.Should().Be(FieldChangeState.CompletedWithSkips);
        return (seeded, admin, id);
    }

    private Task<HttpResponseMessage> RetryAsync(HttpClient client, Guid id) =>
        client.PostAsync($"/api/content-types/field-changes/{id}/retry", null, Ct);

    /// <summary>
    /// Runs a recompute over <paramref name="count"/> entries in batches of <paramref name="batchSize"/>
    /// and checks the entries at <paramref name="failing"/> were skipped and every other one written.
    /// </summary>
    private async Task AssertSkipsAsync(int count, int batchSize, params int[] failing)
    {
        var seeded = await SeedAsync(count);
        foreach (var n in failing)
        {
            _failing.Add(seeded.Ids[n]);
        }

        var id = await StoreRunningAsync(seeded);
        var done = await DriveAsync(seeded.Slug, id, batchSize);

        done.State.Should().Be(FieldChangeState.CompletedWithSkips);
        done.CompletedAt.Should().NotBeNull();
        done.SkippedCount.Should().Be(failing.Length);
        done.Skipped!.Select(k => k.EntryId).Should().BeEquivalentTo(failing.Select(n => seeded.Ids[n]));
        done.Skipped.Should().OnlyContain(k => k.Reason == "InvalidOperationException");
        done.Processed.Should().Be(count - failing.Length);
        done.Error.Should().BeNull();

        var entries = await EntriesAsync(seeded);
        for (var n = 0; n < count; n++)
        {
            var skipped = failing.Contains(n);
            Label(entries[seeded.Ids[n]]).Should().Be(skipped ? "stale" : $"{Secret} {seeded.Ids[n].ToString()[^2..]}".ToUpperInvariant());
            (await EventCountAsync(seeded.Slug, seeded.Ids[n])).Should().Be(skipped ? 0 : 1);
        }
    }

    // What is skipped.

    [Fact]
    public Task A_middle_entry_that_fails_is_skipped_and_the_other_two_are_written() => AssertSkipsAsync(3, FieldChanges.BatchSize, 1);

    [Fact]
    public Task A_first_entry_that_fails_is_skipped_and_the_rest_are_written() => AssertSkipsAsync(3, FieldChanges.BatchSize, 0);

    [Fact]
    public Task A_last_entry_that_fails_is_skipped_and_the_entries_before_it_are_written() => AssertSkipsAsync(3, FieldChanges.BatchSize, 2);

    [Fact]
    public Task Entries_that_fail_in_a_later_batch_are_skipped_and_the_other_batches_are_written() => AssertSkipsAsync(6, 2, 3, 4);

    [Fact]
    public Task Two_entries_that_fail_in_one_batch_are_both_skipped_and_the_others_written() => AssertSkipsAsync(5, FieldChanges.BatchSize, 1, 3);

    [Fact]
    public void A_skip_reason_is_the_exception_type_and_for_a_uniqueness_refusal_the_rule_name()
    {
        FieldChanges.SkipReason(new EntryFailure(Guid.NewGuid(), new InvalidOperationException(Secret))).Should().Be("InvalidOperationException");
        FieldChanges.SkipReason(new EntryFailure(Guid.NewGuid(), new ContentUniquenessException("t", "rule-x", null)))
            .Should().Be("ContentUniquenessException (rule 'rule-x')");
    }

    [Fact]
    public async Task A_write_to_the_type_is_accepted_after_the_change_ends_completed_with_skips()
    {
        var seeded = await SeedAsync(3);
        var admin = await TenantProfileSeed.AdminInAsync(_fixture, seeded.Slug, Ct);
        _failing.Add(seeded.Ids[1]);
        var id = await StoreRunningAsync(seeded);
        object body = new { contentType = seeded.Type, data = new Dictionary<string, object> { ["Name"] = "fresh" } };
        (await admin.PostAsJsonAsync("/api/contents", body, Ct)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "the positive control: the type is write-locked while the change runs");

        (await DriveAsync(seeded.Slug, id)).State.Should().Be(FieldChangeState.CompletedWithSkips);

        var written = await admin.PostAsJsonAsync("/api/contents", body, Ct);
        written.StatusCode.Should().Be(HttpStatusCode.OK, await written.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task The_completion_audit_carries_ids_and_counts_and_not_a_value_or_a_message()
    {
        var seeded = await SeedAsync(3);
        var admin = await TenantProfileSeed.AdminInAsync(_fixture, seeded.Slug, Ct);
        _failing.Add(seeded.Ids[1]);
        var id = await StoreRunningAsync(seeded);

        await DriveAsync(seeded.Slug, id);

        var row = (await AuditAsync(seeded.Slug, "contenttype.field_change.completed_with_skips", id)).Should().ContainSingle().Subject;
        row.TargetType.Should().Be(nameof(FieldChange));
        row.Metadata!.Keys.Should().BeEquivalentTo("contentType", "field", "kind", "processed", "total", "skippedCount", "skippedIds");
        var audit = JsonSerializer.Serialize(row.Metadata);
        audit.Should().Contain(seeded.Ids[1].ToString()).And.Contain("\"skippedCount\":1").And.Contain("\"processed\":2");
        audit.Should().NotContain(Secret).And.NotContain("cannot rewrite");

        var status = await admin.GetAsync($"/api/content-types/field-changes/{id}", Ct);
        var wire = await status.Content.ReadAsStringAsync(Ct);
        status.StatusCode.Should().Be(HttpStatusCode.OK, wire);
        wire.Should().Contain("\"completed_with_skips\"").And.Contain(seeded.Ids[1].ToString());
        wire.Should().NotContain(Secret).And.NotContain("cannot rewrite");

        JsonSerializer.Serialize(await StoredAsync(seeded.Slug, id)).Should().NotContain(Secret).And.NotContain("cannot rewrite");
    }

    [Fact]
    public async Task More_than_a_hundred_failures_record_a_hundred_ids_and_count_them_all()
    {
        var seeded = await SeedAsync(FieldChange.MaxSkipped + 5);
        _failing.UnionWith(seeded.Ids);
        var id = await StoreRunningAsync(seeded);

        var done = await DriveAsync(seeded.Slug, id);

        done.State.Should().Be(FieldChangeState.CompletedWithSkips);
        done.Skipped.Should().HaveCount(FieldChange.MaxSkipped);
        done.SkippedCount.Should().Be(FieldChange.MaxSkipped + 5);
        done.Processed.Should().Be(0);
        var row = (await AuditAsync(seeded.Slug, "contenttype.field_change.completed_with_skips", id)).Should().ContainSingle().Subject;
        JsonSerializer.Serialize(row.Metadata).Should().Contain($"\"skippedCount\":{FieldChange.MaxSkipped + 5}");
    }

    // What counts as an entry failure.

    [Fact]
    public void A_plain_exception_is_an_entry_failure() =>
        FieldChanges.IsEntryFailure(new InvalidOperationException("x")).Should().BeTrue();

    [Fact]
    public void A_uniqueness_refusal_is_an_entry_failure() =>
        FieldChanges.IsEntryFailure(new ContentUniquenessException("t", "rule", null)).Should().BeTrue();

    [Fact]
    public void A_uniqueness_check_that_could_not_finish_is_not_an_entry_failure() =>
        FieldChanges.IsEntryFailure(ContentUniquenessException.InProgress("t", "rule", null)).Should().BeFalse();

    [Fact]
    public void A_cancellation_is_not_an_entry_failure() =>
        FieldChanges.IsEntryFailure(new OperationCanceledException()).Should().BeFalse();

    [Fact]
    public void A_timeout_is_not_an_entry_failure() =>
        FieldChanges.IsEntryFailure(new TimeoutException()).Should().BeFalse();

    [Fact]
    public void A_database_exception_is_not_an_entry_failure() =>
        FieldChanges.IsEntryFailure(new NpgsqlException("down")).Should().BeFalse();

    [Fact]
    public void A_database_exception_inside_another_exception_is_not_an_entry_failure() =>
        FieldChanges.IsEntryFailure(new InvalidOperationException("outer", new InvalidOperationException("mid", new NpgsqlException("down"))))
            .Should().BeFalse();

    [Fact]
    public void A_lost_concurrency_check_is_not_an_entry_failure() =>
        FieldChanges.IsEntryFailure(new JasperFx.ConcurrencyException("lost", new InvalidOperationException("inner"))).Should().BeFalse();

    [Fact]
    public void A_stream_id_collision_is_not_an_entry_failure() =>
        FieldChanges.IsEntryFailure(new Marten.Exceptions.ExistingStreamIdCollisionException(Guid.NewGuid(), typeof(Content))).Should().BeFalse();

    [Fact]
    public void A_failed_marten_command_is_not_an_entry_failure() =>
        FieldChanges.IsEntryFailure(new Marten.Exceptions.MartenCommandException(new NpgsqlCommand("select 1"), new InvalidOperationException("x")))
            .Should().BeFalse();

    // Only a recompute skips.

    [Fact]
    public async Task A_rename_whose_entry_fails_records_the_error_and_stays_running_with_no_skip()
    {
        var seeded = await SeedAsync(3);
        _failing.Add(seeded.Ids[1]);
        var id = await StoreChangeAsync(seeded.Slug, seeded.Type, c =>
        {
            c.Kind = FieldChangeKind.Rename;
            c.Field = "Name";
            c.NewName = "FullName";
            c.Total = 3;
        });

        var handler = new FieldChangeCommandHandler(Store, _fixture.Services.GetRequiredService<IConfiguration>());
        var run = () => handler.ExecuteAsync(new FieldChangeCommand { TenantId = seeded.Slug, ChangeId = id }, Ct);

        await run.Should().ThrowAsync<InvalidOperationException>();
        var stored = await StoredAsync(seeded.Slug, id);
        stored.State.Should().Be(FieldChangeState.Running);
        stored.Error.Should().Contain("InvalidOperationException");
        stored.SkippedCount.Should().Be(0);
        stored.Skipped.Should().BeNull();
    }

    [Fact]
    public async Task A_recompute_that_fails_on_a_transient_error_records_it_and_stays_running_with_no_skip()
    {
        var seeded = await SeedAsync(3);
        var before = FieldChanges.BeforeEntryForTests;
        FieldChanges.BeforeEntryForTests = (content, _) => content.Id == seeded.Ids[1]
            ? throw new TimeoutException("slow")
            : Task.CompletedTask;
        var id = await StoreRunningAsync(seeded);

        var handler = new FieldChangeCommandHandler(Store, _fixture.Services.GetRequiredService<IConfiguration>());
        var run = () => handler.ExecuteAsync(new FieldChangeCommand { TenantId = seeded.Slug, ChangeId = id }, Ct);

        await run.Should().ThrowAsync<TimeoutException>();
        FieldChanges.BeforeEntryForTests = before;
        var stored = await StoredAsync(seeded.Slug, id);
        stored.State.Should().Be(FieldChangeState.Running);
        stored.Error.Should().Contain("TimeoutException");
        stored.SkippedCount.Should().Be(0);
        stored.Skipped.Should().BeNull();
    }

    // Retry.

    [Fact]
    public async Task A_retry_recomputes_only_the_skipped_entries_and_ends_completed()
    {
        var (seeded, admin, first) = await SkippedAsync(4, 1, 3);
        await using (var session = Store.LightweightSession(seeded.Slug))
        {
            var untouched = (await session.LoadAsync<Content>(seeded.Ids[0], Ct))!;
            untouched.Data["Label"] = "tampered";
            session.Store(untouched);
            await session.SaveChangesAsync(Ct);
        }

        _failing.Clear();
        var started = await RetryAsync(admin, first);

        started.StatusCode.Should().Be(HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync(Ct));
        var body = await started.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("total").GetInt32().Should().Be(2);
        body.GetProperty("retryOf").GetGuid().Should().Be(first);
        var retry = body.GetProperty("id").GetGuid();

        var done = await DriveAsync(seeded.Slug, retry);

        done.State.Should().Be(FieldChangeState.Completed);
        done.RetryOf.Should().Be(first);
        done.EntryIds.Should().BeEquivalentTo([seeded.Ids[1], seeded.Ids[3]]);
        done.Processed.Should().Be(2);
        var entries = await EntriesAsync(seeded);
        Label(entries[seeded.Ids[1]]).Should().NotBe("stale");
        Label(entries[seeded.Ids[3]]).Should().NotBe("stale");
        Label(entries[seeded.Ids[0]]).Should().Be("tampered", "an entry that was not skipped is not visited");
        (await StoredAsync(seeded.Slug, first)).RetriedBy.Should().Be(retry);
    }

    [Fact]
    public async Task A_retry_is_audited_with_both_ids_and_counts()
    {
        var (seeded, admin, first) = await SkippedAsync(3, 1);
        _failing.Clear();

        var started = await RetryAsync(admin, first);

        var retry = (await started.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var row = (await AuditAsync(seeded.Slug, "contenttype.field_change.retried", retry)).Should().ContainSingle().Subject;
        row.Metadata!.Keys.Should().BeEquivalentTo("contentType", "field", "retryOf", "change", "skippedCount", "total", "fullPass");
        JsonSerializer.Serialize(row.Metadata).Should().Contain(first.ToString()).And.NotContain(Secret);
    }

    [Fact]
    public async Task A_second_retry_is_refused_and_names_the_first_retry()
    {
        var (seeded, admin, first) = await SkippedAsync(3, 1);
        _failing.Clear();
        var started = await RetryAsync(admin, first);
        var retry = (await started.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();

        var again = await RetryAsync(admin, first);

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await again.Content.ReadAsStringAsync(Ct)).Should().Contain(retry.ToString());
        (await StoredAsync(seeded.Slug, first)).RetriedBy.Should().Be(retry);
    }

    [Theory]
    [InlineData(FieldChangeState.Running)]
    [InlineData(FieldChangeState.Completed)]
    [InlineData(FieldChangeState.Abandoned)]
    public async Task A_change_that_did_not_complete_with_skips_is_not_retried(FieldChangeState state)
    {
        var (slug, admin) = await TenantAsync();
        var type = NewName();
        await using (var session = Store.LightweightSession(slug))
        {
            session.Store(new ContentTypeDefinition { Id = Guid.NewGuid(), Name = type, DisplayName = type, Fields = [Plain("Name")] });
            await session.SaveChangesAsync(Ct);
        }

        var id = await StoreChangeAsync(slug, type, c => c.State = state);

        var res = await RetryAsync(admin, id);

        res.StatusCode.Should().Be(HttpStatusCode.Conflict, await res.Content.ReadAsStringAsync(Ct));
        (await StoredAsync(slug, id)).RetriedBy.Should().BeNull();
    }

    [Fact]
    public async Task A_retry_while_another_change_runs_on_the_type_is_refused()
    {
        var (seeded, admin, first) = await SkippedAsync(3, 1);
        _failing.Clear();
        var running = await StoreChangeAsync(seeded.Slug, seeded.Type, total: 3);

        var res = await RetryAsync(admin, first);

        res.StatusCode.Should().Be(HttpStatusCode.Conflict, await res.Content.ReadAsStringAsync(Ct));
        (await StoredAsync(seeded.Slug, first)).RetriedBy.Should().BeNull();
        (await StoredAsync(seeded.Slug, running)).State.Should().Be(FieldChangeState.Running);
    }

    [Fact]
    public async Task Another_tenants_change_is_not_found_to_retry()
    {
        var (seeded, _, first) = await SkippedAsync(3, 1);
        _failing.Clear();
        var (_, stranger) = await TenantAsync();

        var res = await RetryAsync(stranger, first);

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await StoredAsync(seeded.Slug, first)).RetriedBy.Should().BeNull();
        (await RetryAsync(stranger, Guid.NewGuid())).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_retry_needs_the_manage_content_types_capability()
    {
        var (seeded, admin, first) = await SkippedAsync(3, 1);
        _failing.Clear();

        (await RetryAsync(_fixture.CreateClient(), first)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var user = _fixture.CreateClient();
        user.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await _fixture.StoredUserTokenAsync("User"));
        (await RetryAsync(user, first)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StoredAsync(seeded.Slug, first)).RetriedBy.Should().BeNull();

        (await RetryAsync(admin, first)).StatusCode.Should().Be(HttpStatusCode.Accepted, "the positive control: an admin");
    }

    [Fact]
    public async Task A_retry_of_more_skips_than_were_recorded_runs_a_full_pass()
    {
        var seeded = await SeedAsync(3);
        var admin = await TenantProfileSeed.AdminInAsync(_fixture, seeded.Slug, Ct);
        var first = await StoreChangeAsync(seeded.Slug, seeded.Type, c =>
        {
            c.State = FieldChangeState.CompletedWithSkips;
            c.CompletedAt = DateTimeOffset.UtcNow;
            c.SkippedCount = FieldChange.MaxSkipped + 50;
            c.Skipped = [new FieldChangeSkip { EntryId = seeded.Ids[0], Reason = "InvalidOperationException", At = DateTimeOffset.UtcNow }];
        });

        var started = await RetryAsync(admin, first);

        started.StatusCode.Should().Be(HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync(Ct));
        var retry = (await started.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var done = await DriveAsync(seeded.Slug, retry);

        done.State.Should().Be(FieldChangeState.Completed);
        done.EntryIds.Should().BeNull();
        done.Total.Should().Be(3);
        (await EntriesAsync(seeded)).Values.Should().OnlyContain(e => Label(e) != "stale");
        var row = (await AuditAsync(seeded.Slug, "contenttype.field_change.retried", retry)).Should().ContainSingle().Subject;
        JsonSerializer.Serialize(row.Metadata).Should().Contain("\"fullPass\":true");
    }

    [Fact]
    public async Task A_change_that_completed_with_skips_is_neither_resumed_nor_abandoned()
    {
        var (_, admin, id) = await SkippedAsync(3, 1);

        var resume = await admin.PostAsync($"/api/content-types/field-changes/{id}/resume", null, Ct);
        var abandon = await admin.PostAsync($"/api/content-types/field-changes/{id}/abandon", null, Ct);

        resume.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resume.Content.ReadAsStringAsync(Ct)).Should().Contain("completed with skips");
        abandon.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await abandon.Content.ReadAsStringAsync(Ct)).Should().Contain("completed with skips");
    }

    // The list.

    [Fact]
    public async Task The_list_is_newest_first_and_paged()
    {
        var (slug, admin) = await TenantAsync();
        var type = NewName();
        await using (var session = Store.LightweightSession(slug))
        {
            session.Store(new ContentTypeDefinition { Id = Guid.NewGuid(), Name = type, DisplayName = type, Fields = [Plain("Name")] });
            await session.SaveChangesAsync(Ct);
        }

        var now = DateTimeOffset.UtcNow;
        var oldest = await StoreChangeAsync(slug, type, c => { c.State = FieldChangeState.Completed; c.StartedAt = now.AddDays(-3); });
        var newest = await StoreChangeAsync(slug, type, c => { c.State = FieldChangeState.Completed; c.StartedAt = now.AddDays(-1); });
        var middle = await StoreChangeAsync(slug, type, c => { c.State = FieldChangeState.Completed; c.StartedAt = now.AddDays(-2); });
        var otherType = NewName();
        await StoreChangeAsync(slug, otherType, c => { c.State = FieldChangeState.Completed; c.StartedAt = now; });

        var first = await admin.GetFromJsonAsync<JsonElement>($"/api/content-types/{type}/field-changes?page=1&pageSize=2", Ct);
        var second = await admin.GetFromJsonAsync<JsonElement>($"/api/content-types/{type}/field-changes?page=2&pageSize=2", Ct);
        var beyond = await admin.GetFromJsonAsync<JsonElement>($"/api/content-types/{type}/field-changes?page=3&pageSize=2", Ct);

        first.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().Equal(newest, middle);
        first.GetProperty("totalItems").GetInt32().Should().Be(3);
        second.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().Equal(oldest);
        beyond.GetProperty("items").GetArrayLength().Should().Be(0);
        beyond.GetProperty("totalItems").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task The_list_of_a_type_with_no_changes_is_an_empty_page_and_an_unknown_type_is_not_found()
    {
        var (slug, admin) = await TenantAsync();
        var type = NewName();
        await using (var session = Store.LightweightSession(slug))
        {
            session.Store(new ContentTypeDefinition { Id = Guid.NewGuid(), Name = type, DisplayName = type, Fields = [Plain("Name")] });
            await session.SaveChangesAsync(Ct);
        }

        var empty = await admin.GetFromJsonAsync<JsonElement>($"/api/content-types/{type}/field-changes", Ct);

        empty.GetProperty("items").GetArrayLength().Should().Be(0);
        empty.GetProperty("totalItems").GetInt32().Should().Be(0);
        (await admin.GetAsync($"/api/content-types/{NewName()}/field-changes", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_list_leaves_out_another_tenants_changes_and_needs_the_capability()
    {
        var (mine, admin) = await TenantAsync();
        var (theirs, _) = await TenantAsync();
        var type = NewName();
        foreach (var slug in new[] { mine, theirs })
        {
            await using var session = Store.LightweightSession(slug);
            session.Store(new ContentTypeDefinition { Id = Guid.NewGuid(), Name = type, DisplayName = type, Fields = [Plain("Name")] });
            await session.SaveChangesAsync(Ct);
        }

        var own = await StoreChangeAsync(mine, type, c => c.State = FieldChangeState.Completed);
        var other = await StoreChangeAsync(theirs, type, c => c.State = FieldChangeState.Completed);

        var list = await admin.GetFromJsonAsync<JsonElement>($"/api/content-types/{type}/field-changes", Ct);

        var ids = list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        ids.Should().Equal(own);
        ids.Should().NotContain(other);

        (await _fixture.CreateClient().GetAsync($"/api/content-types/{type}/field-changes", Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var user = _fixture.CreateClient();
        user.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", await _fixture.StoredUserTokenAsync("User"));
        (await user.GetAsync($"/api/content-types/{type}/field-changes", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // Rows written before the change.

    [Fact]
    public async Task A_change_stored_without_the_new_fields_loads_and_reads_as_before()
    {
        var (slug, admin) = await TenantAsync();
        var type = NewName();
        var id = await StoreChangeAsync(slug, type, c =>
        {
            c.State = FieldChangeState.Completed;
            c.CompletedAt = DateTimeOffset.UtcNow;
            c.Processed = 3;
            c.Total = 3;
        });

        await using (var connection = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(
                "update public.mt_doc_fieldchange set data = data - 'Skipped' - 'SkippedCount' - 'RetryOf' - 'RetriedBy' - 'EntryIds' "
                + "where id = @id returning (data ? 'SkippedCount')", connection);
            command.Parameters.AddWithValue("id", id);
            (await command.ExecuteScalarAsync(Ct)).Should().Be(false, "the row no longer carries the new fields");
        }

        var stored = await StoredAsync(slug, id);
        stored.State.Should().Be(FieldChangeState.Completed);
        stored.SkippedCount.Should().Be(0);
        stored.Skipped.Should().BeNull();
        stored.RetryOf.Should().BeNull();
        stored.RetriedBy.Should().BeNull();
        stored.EntryIds.Should().BeNull();

        var status = await admin.GetFromJsonAsync<JsonElement>($"/api/content-types/field-changes/{id}", Ct);
        status.GetProperty("state").GetString().Should().Be("completed");
        status.GetProperty("processed").GetInt32().Should().Be(3);
        (!status.TryGetProperty("skippedCount", out var skipped) || skipped.ValueKind == JsonValueKind.Null)
            .Should().BeTrue("a change that skipped nothing reports no skips");
    }
}
