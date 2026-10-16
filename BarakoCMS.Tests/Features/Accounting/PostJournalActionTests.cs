using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using barakoCMS.Modules;
using BarakoCMS.Accounting;
using BarakoCMS.Accounting.Domain;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Accounting;

/// <summary>Accounting.PostJournal posts a mapping's lines once for a subject and key, whatever is retried.</summary>
[Collection("Sequential")]
public class PostJournalActionTests(IntegrationTestFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Setup(string Cash, string Income, string Mapping);

    private async Task<Setup> SetupAsync(bool skipIfZero = true, params (string Side, string Amount)[] lines)
    {
        var cash = await AccountingTestData.AccountAsync(fixture, "1010", AccountType.Asset);
        var income = await AccountingTestData.AccountAsync(fixture, "4010", AccountType.Income);
        var name = $"map-{Guid.NewGuid():N}"[..14];
        if (lines.Length == 0)
            lines = [("debit", "{{Data.Total}}"), ("credit", "{{Data.Total}}")];

        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new AccountMapping
        {
            Name = name,
            Rule = new MappingRule(
                [.. lines.Select(l => new MappingLine(l.Side == "debit" ? cash : income, l.Side, l.Amount))],
                skipIfZero, null),
        });
        await session.SaveChangesAsync(Ct);
        return new Setup(cash, income, name);
    }

    private static Content Subject(decimal total, Guid? branch = null) => new()
    {
        Id = Guid.NewGuid(),
        ContentType = "receipt",
        BranchId = branch,
        Data = new Dictionary<string, object> { ["Total"] = total },
    };

    private async Task<WorkflowActionResult> RunAsync(
        Content subject, string mapping, string actionKey, Guid? runId = null, string? amount = null, string? mappingName = null)
    {
        var parameters = new Dictionary<string, string>
        {
            ["Mapping"] = mappingName ?? mapping,
            ["ActionKey"] = actionKey,
            ["Date"] = "2026-10-05",
            ["RunId"] = (runId ?? Guid.NewGuid()).ToString(),
        };
        if (amount is not null) parameters["Amount"] = amount;

        using var scope = fixture.Services.CreateScope();
        IWorkflowAction action = ActivatorUtilities.CreateInstance<PostJournalAction>(scope.ServiceProvider);
        return await action.RunAsync(new WorkflowContext { Parameters = parameters, Content = subject, TriggerEvent = "Created" }, Ct);
    }

    private Task<List<Content>> EntriesForAsync(Content subject) =>
        AccountingTestData.EntriesAsync(fixture, $"{subject.Id:N}:");

    [Fact]
    public async Task A_receipt_posts_a_balanced_entry_to_the_mapped_accounts()
    {
        var setup = await SetupAsync();
        var subject = Subject(1500m);

        var result = await RunAsync(subject, setup.Mapping, "receipt");

        result.Succeeded.Should().BeTrue(result.Error);
        var entry = (await EntriesForAsync(subject)).Single();
        AccountingTestData.Lines(entry).Should().Equal(
            (setup.Cash, 1500m, 0m), (setup.Income, 0m, 1500m));
        entry.Data["Amount"].Should().Be(1500m);
    }

    [Fact]
    public async Task The_same_subject_and_key_twice_returns_the_same_id_and_posts_once()
    {
        var setup = await SetupAsync();
        var subject = Subject(900m);

        var first = await RunAsync(subject, setup.Mapping, "receipt");
        var second = await RunAsync(subject, setup.Mapping, "receipt");

        second.Succeeded.Should().BeTrue(second.Error);
        second.Outputs["id"].Should().Be(first.Outputs["id"]);
        (await EntriesForAsync(subject)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_different_key_posts_a_second_entry()
    {
        var setup = await SetupAsync();
        var subject = Subject(300m);

        var first = await RunAsync(subject, setup.Mapping, "receipt");
        var second = await RunAsync(subject, setup.Mapping, "adjustment");

        second.Outputs["id"].Should().NotBe(first.Outputs["id"]);
        (await EntriesForAsync(subject)).Should().HaveCount(2);
    }

    [Fact]
    public async Task SkipIfZero_posts_only_the_nonzero_side_and_nothing_when_none_remain()
    {
        var setup = await SetupAsync(true, ("debit", "{{Data.Over}}"), ("debit", "{{Data.Short}}"), ("credit", "{{Data.Total}}"));
        var subject = Subject(50m);
        subject.Data["Over"] = 0m;
        subject.Data["Short"] = 50m;

        (await RunAsync(subject, setup.Mapping, "count")).Succeeded.Should().BeTrue();
        AccountingTestData.Lines((await EntriesForAsync(subject)).Single()).Should().HaveCount(2);

        var empty = Subject(0m);
        empty.Data["Over"] = 0m;
        empty.Data["Short"] = 0m;
        var none = await RunAsync(empty, setup.Mapping, "count");

        none.Succeeded.Should().BeTrue(none.Error);
        none.Outputs["id"].Should().BeEmpty();
        none.Created.Should().BeNull();
        (await EntriesForAsync(empty)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_mapping_or_account_is_a_permanent_failure()
    {
        var setup = await SetupAsync();

        var noMapping = await RunAsync(Subject(10m), setup.Mapping, "receipt", mappingName: "no-such-mapping");
        noMapping.Succeeded.Should().BeFalse();
        noMapping.Retryable.Should().BeFalse();

        var missing = AccountingTestData.Code("GONE");
        using (var scope = fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new AccountMapping
            {
                Name = "bad-" + setup.Mapping,
                Rule = new MappingRule([new(missing, "debit", "5"), new(setup.Income, "credit", "5")], false, null),
            });
            await session.SaveChangesAsync(Ct);
        }

        var unknownAccount = await RunAsync(Subject(10m), setup.Mapping, "receipt", mappingName: "bad-" + setup.Mapping);
        unknownAccount.Succeeded.Should().BeFalse();
        unknownAccount.Retryable.Should().BeFalse();
        unknownAccount.Error.Should().Contain(missing);
    }

    [Fact]
    public async Task The_entry_records_the_subject_the_run_the_branch_and_one_created_ref()
    {
        var setup = await SetupAsync();
        var branch = Guid.NewGuid();
        var subject = Subject(75m, branch);
        var runId = Guid.NewGuid();

        var result = await RunAsync(subject, setup.Mapping, "receipt", runId);

        var entry = (await EntriesForAsync(subject)).Single();
        AccountingTestData.Text(entry, "Reference").Should().StartWith($"{subject.Id:N}:");
        entry.SourceRunId.Should().Be(runId);
        entry.BranchId.Should().Be(branch);
        result.Created.Should().ContainSingle().Which.Should().Be(
            new CreatedRef("journalEntry", entry.Id, AccountingTestData.Text(entry, "EntryNumber")));
        result.Outputs["id"].Should().Be(entry.Id.ToString());
    }

    [Fact]
    public async Task A_line_of_Amount_takes_the_Amount_parameter()
    {
        var setup = await SetupAsync(true, ("debit", "{{Amount}}"), ("credit", "{{Amount}}"));
        var subject = Subject(1m);

        var result = await RunAsync(subject, setup.Mapping, "receipt", amount: "245.50");

        result.Succeeded.Should().BeTrue(result.Error);
        (await EntriesForAsync(subject)).Single().Data["Amount"].Should().Be(245.50m);
    }

    [Fact]
    public async Task A_line_text_that_is_not_an_amount_is_a_permanent_failure()
    {
        var setup = await SetupAsync(true, ("debit", "lots"), ("credit", "lots"));
        var subject = Subject(1m);

        var result = await RunAsync(subject, setup.Mapping, "receipt");

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain("Line 1").And.Contain("lots");
        (await EntriesForAsync(subject)).Should().BeEmpty();
    }

    [Fact]
    public void The_module_registers_the_action_under_its_prefix()
    {
        var services = new ServiceCollection();
        new AccountingModule().ConfigureWorkflowActions(new ModuleWorkflowActionRegistry().For(services, new AccountingModule()));

        services.Should().Contain(d => d.ServiceType == typeof(IWorkflowAction));
    }
}
