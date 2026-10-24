using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using BarakoCMS.Accounting;
using BarakoCMS.Accounting.Domain;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BarakoCMS.Tests.Features.Accounting;

/// <summary>
/// A journal entry is Posted when it is made and Voided by the Void transition, and a Voided entry
/// stays in the books, counted on its own date, and its reversal counts on the reversal date. An upgraded database reads the same way after the 5.1.0 migration.
/// </summary>
[Collection("Sequential")]
public class JournalLifecycleTests(IntegrationTestFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(string Cash, string Income, Guid Entry)> PostedAsync(decimal amount = 100m, int month = 10)
    {
        var cash = await AccountingTestData.AccountAsync(fixture, "1000", AccountType.Asset);
        var income = await AccountingTestData.AccountAsync(fixture, "4000", AccountType.Income);
        var id = await AccountingTestData.PostAsync(fixture, new DateOnly(2026, month, 5), cash, income, amount);
        return (cash, income, id);
    }

    private async Task<ContentTransitionResult> VoidAsync(Guid id, string? reason)
    {
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var entry = (await session.LoadAsync<Content>(id, Ct))!;
        return await scope.ServiceProvider.GetRequiredService<IContentTransitioner>().TransitionAsync(
            entry, "Void", ContentTransitionActor.ForSystem("accounting"),
            new ContentTransitionOptions
            {
                SkipPermissionChecks = true,
                Data = reason is null ? null : new Dictionary<string, object> { ["VoidReason"] = reason },
            }, Ct);
    }

    [Fact]
    public async Task A_posted_entry_is_in_the_Posted_state()
    {
        var (_, _, id) = await PostedAsync();

        (await AccountingTestData.LoadAsync(fixture, id)).LifecycleState.Should().Be("Posted");
    }

    [Fact]
    public async Task Void_needs_a_reason_and_moves_the_entry_to_Voided_with_one()
    {
        var (_, _, id) = await PostedAsync();

        (await VoidAsync(id, reason: null)).Outcome.Should().Be(ContentTransitionOutcome.Invalid);
        var voided = await VoidAsync(id, "posted twice");

        voided.Outcome.Should().Be(ContentTransitionOutcome.Transitioned, string.Join("; ", voided.Errors));
        (await AccountingTestData.LoadAsync(fixture, id)).LifecycleState.Should().Be("Voided");
    }

    [Fact]
    public async Task A_voided_entry_stays_in_balances_and_cash_flow()
    {
        var (cash, income, id) = await PostedAsync(250m, month: 11);

        (await VoidAsync(id, "wrong account")).Succeeded.Should().BeTrue();

        using var after = fixture.Services.CreateScope();
        var later = after.ServiceProvider.GetRequiredService<ReportingService>();
        (await later.BalancesAsync(null, Ct)).Single(b => b.Code == income).Balance.Should().Be(250m);
        var flow = await later.CashFlowAsync([cash], 2026, 11, Ct);
        flow.Receipts.Should().Be(250m);
        flow.Lines.Should().HaveCount(1);
    }

    [Fact]
    public async Task An_entry_reversed_in_the_next_month_still_shows_in_its_own_month()
    {
        var cash = await AccountingTestData.AccountAsync(fixture, "1000", AccountType.Asset);
        var income = await AccountingTestData.AccountAsync(fixture, "4000", AccountType.Income);
        var original = await AccountingTestData.PostAsync(fixture, new DateOnly(2026, 3, 10), cash, income, 300m);
        await AccountingTestData.PostAsync(fixture, new DateOnly(2026, 4, 2), income, cash, 300m);
        (await VoidAsync(original, "cancelled")).Succeeded.Should().BeTrue();

        using var scope = fixture.Services.CreateScope();
        var reports = scope.ServiceProvider.GetRequiredService<ReportingService>();

        (await reports.BalancesAsync(new DateOnly(2026, 3, 31), Ct)).Single(b => b.Code == cash).Balance.Should().Be(300m);
        var march = await reports.CashFlowAsync([cash], 2026, 3, Ct);
        march.Receipts.Should().Be(300m);
        march.Closing.Should().Be(300m);
        var april = await reports.CashFlowAsync([cash], 2026, 4, Ct);
        april.Disbursements.Should().Be(300m);
        april.Opening.Should().Be(300m);
    }

    [Fact]
    public async Task A_report_after_the_reversal_date_nets_the_pair_to_zero()
    {
        var cash = await AccountingTestData.AccountAsync(fixture, "1000", AccountType.Asset);
        var income = await AccountingTestData.AccountAsync(fixture, "4000", AccountType.Income);
        var original = await AccountingTestData.PostAsync(fixture, new DateOnly(2026, 3, 10), cash, income, 300m);
        await AccountingTestData.PostAsync(fixture, new DateOnly(2026, 4, 2), income, cash, 300m);
        (await VoidAsync(original, "cancelled")).Succeeded.Should().BeTrue();

        using var scope = fixture.Services.CreateScope();
        var balances = await scope.ServiceProvider.GetRequiredService<ReportingService>().BalancesAsync(new DateOnly(2026, 4, 30), Ct);

        balances.Single(b => b.Code == cash).Balance.Should().Be(0m);
        balances.Single(b => b.Code == income).Balance.Should().Be(0m);
    }

    private async Task<IReadOnlyList<string>> UpdateErrorsAsync(Guid id, Action<Dictionary<string, object>> change)
    {
        var entry = await AccountingTestData.LoadAsync(fixture, id);
        var data = new Dictionary<string, object>(entry.Data);
        change(data);
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<barakoCMS.Infrastructure.Services.IContentLifecycleRunner>()
            .RunBeforeSaveAsync(AccountingContentTypes.JournalEntry, id, data, entry.Data, Guid.NewGuid(), Ct);
    }

    private static void ChangeLines(Dictionary<string, object> data) => data["Lines"] = new List<object>
    {
        new Dictionary<string, object> { ["AccountCode"] = "X", ["Debit"] = 5m, ["Credit"] = 0m },
        new Dictionary<string, object> { ["AccountCode"] = "Y", ["Debit"] = 0m, ["Credit"] = 5m },
    };

    [Fact]
    public async Task An_update_that_sets_Status_to_Voided_is_refused()
    {
        var (_, _, id) = await PostedAsync();

        var errors = await UpdateErrorsAsync(id, d => d["Status"] = "Voided");

        errors.Should().Contain(e => e.Contains("changes only by voiding"));
        (await AccountingTestData.LoadAsync(fixture, id)).LifecycleState.Should().Be("Posted");
    }

    [Fact]
    public async Task A_posted_entrys_lines_cannot_be_changed()
    {
        var (_, _, id) = await PostedAsync();

        (await UpdateErrorsAsync(id, ChangeLines)).Should().Contain(e => e.Contains("cannot be edited"));
    }

    [Fact]
    public async Task A_posted_entrys_date_reference_and_amount_cannot_be_changed()
    {
        var (_, _, id) = await PostedAsync();
        var before = await AccountingTestData.LoadAsync(fixture, id);

        (await UpdateErrorsAsync(id, d => d["Date"] = "2026-01-02")).Should().Contain(e => e.Contains("date of a posted"));
        (await UpdateErrorsAsync(id, d => d["Reference"] = "other-ref")).Should().Contain(e => e.Contains("Reference of a posted"));
        (await UpdateErrorsAsync(id, d => d["Amount"] = 1m)).Should().Contain(e => e.Contains("amount of a posted"));

        var after = await AccountingTestData.LoadAsync(fixture, id);
        AccountingTestData.Text(after, "Date").Should().Be(AccountingTestData.Text(before, "Date"));
        AccountingTestData.Text(after, "Reference").Should().Be(AccountingTestData.Text(before, "Reference"));
    }

    [Fact]
    public async Task A_posted_entry_cannot_be_made_to_void_another()
    {
        var (_, _, id) = await PostedAsync();

        var errors = await UpdateErrorsAsync(id, d => d["VoidsEntryId"] = Guid.NewGuid().ToString());

        errors.Should().Contain(e => e.Contains("VoidsEntryId of a posted"));
        AccountingTestData.Text(await AccountingTestData.LoadAsync(fixture, id), "VoidsEntryId").Should().BeEmpty();
    }

    [Fact]
    public async Task A_posted_entry_updated_with_its_own_values_and_a_new_memo_is_accepted()
    {
        var (_, _, id) = await PostedAsync();

        (await UpdateErrorsAsync(id, d => d["Memo"] = "clearer memo")).Should().BeEmpty();
    }

    [Fact]
    public async Task A_voided_entry_cannot_be_edited_or_have_its_lines_changed()
    {
        var (_, _, id) = await PostedAsync();
        (await VoidAsync(id, "wrong account")).Succeeded.Should().BeTrue();

        (await UpdateErrorsAsync(id, ChangeLines)).Should().Contain(e => e.Contains("voided journal entry cannot be edited"));
        (await UpdateErrorsAsync(id, d => d["Memo"] = "changed")).Should().Contain(e => e.Contains("voided journal entry cannot be edited"));
    }

    [Fact]
    public async Task The_migration_states_legacy_entries_and_adds_the_lifecycle_and_runs_twice_without_change()
    {
        await AccountingTestData.EnsureTypesAsync(fixture);
        var scratch = "journal_lifecycle_" + Guid.NewGuid().ToString("N")[..8];
        var legacyType = "jlmig" + Guid.NewGuid().ToString("N")[..10];
        var posted = Guid.NewGuid();
        var voided = Guid.NewGuid();

        using (var scope = fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            foreach (var (id, status) in new[] { (posted, "Posted"), (voided, "Voided") })
            {
                session.Store(new Content
                {
                    Id = id,
                    ContentType = legacyType,
                    Data = new Dictionary<string, object> { ["Status"] = status },
                });
            }

            await session.SaveChangesAsync(Ct);
        }

        var sql = (await File.ReadAllTextAsync(
                Path.Combine(RepositoryRoot(), "migrations", "5.3.0", "accounting-journal-lifecycle.sql"), Ct))
            .Replace("public.mt_doc_contents", $"{scratch}.mt_doc_contents")
            .Replace("public.mt_doc_contenttypedefinition", $"{scratch}.mt_doc_contenttypedefinition");

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        try
        {
            await ExecuteAsync(connection, $"CREATE SCHEMA {scratch}");
            await ExecuteAsync(connection,
                $"CREATE TABLE {scratch}.mt_doc_contents AS SELECT * FROM public.mt_doc_contents WHERE id IN ('{posted}', '{voided}')");
            await ExecuteAsync(connection,
                $"UPDATE {scratch}.mt_doc_contents SET data = jsonb_set(data, '{{ContentType}}', '\"journalEntry\"')");
            await ExecuteAsync(connection,
                $"CREATE TABLE {scratch}.mt_doc_contenttypedefinition AS SELECT * FROM public.mt_doc_contenttypedefinition "
              + "WHERE data ->> 'Name' = 'journalEntry'");
            // The stored type as a database from before 4.5.0 holds it: no lifecycle, no VoidReason.
            await ExecuteAsync(connection,
                $"UPDATE {scratch}.mt_doc_contenttypedefinition SET data = jsonb_set(data - 'Lifecycle', '{{Fields}}', "
              + "(SELECT jsonb_agg(f) FROM jsonb_array_elements(data -> 'Fields') f WHERE f ->> 'Name' <> 'VoidReason'))");

            (await TextAsync(connection, $"SELECT data ->> 'LifecycleState' FROM {scratch}.mt_doc_contents WHERE id = '{posted}'"))
                .Should().BeNull("the control: a legacy entry starts with no state");

            await ExecuteAsync(connection, sql);
            var afterOnce = await SnapshotAsync(connection, scratch);
            await ExecuteAsync(connection, sql);
            var afterTwice = await SnapshotAsync(connection, scratch);

            (await TextAsync(connection, $"SELECT data ->> 'LifecycleState' FROM {scratch}.mt_doc_contents WHERE id = '{posted}'"))
                .Should().Be("Posted");
            (await TextAsync(connection, $"SELECT data ->> 'LifecycleState' FROM {scratch}.mt_doc_contents WHERE id = '{voided}'"))
                .Should().Be("Voided");

            var stored = JsonDocument.Parse((await TextAsync(connection,
                $"SELECT data::text FROM {scratch}.mt_doc_contenttypedefinition"))!).RootElement;
            var expected = AccountingContentTypes.JournalEntryDefinition().Lifecycle!;
            var lifecycle = stored.GetProperty("Lifecycle");
            lifecycle.GetProperty("InitialState").GetString().Should().Be(expected.InitialState);
            lifecycle.GetProperty("States").EnumerateArray().Select(e => e.GetString()).Should().Equal(expected.States);
            var move = lifecycle.GetProperty("Transitions").EnumerateArray().Single();
            move.GetProperty("Name").GetString().Should().Be("Void");
            move.GetProperty("From").GetString().Should().Be("Posted");
            move.GetProperty("To").GetString().Should().Be("Voided");
            move.GetProperty("RequiredFields").EnumerateArray().Select(e => e.GetString()).Should().Equal("VoidReason");
            stored.GetProperty("Fields").EnumerateArray().Count(f => f.GetProperty("Name").GetString() == "VoidReason")
                .Should().Be(1);

            afterTwice.Should().Be(afterOnce, "a second run finds nothing left to change");
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {scratch} CASCADE");
        }
    }

    private static async Task<string> SnapshotAsync(NpgsqlConnection connection, string scratch) =>
        await TextAsync(connection,
            $"SELECT (SELECT string_agg(data::text, '|' ORDER BY id) FROM {scratch}.mt_doc_contents) "
          + $"|| (SELECT string_agg(data::text, '|' ORDER BY id) FROM {scratch}.mt_doc_contenttypedefinition)") ?? string.Empty;

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<string?> TextAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(Ct) as string;
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "migrations"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
