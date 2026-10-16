using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using BarakoCMS.Accounting;
using BarakoCMS.Accounting.Domain;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Features.Accounting;

/// <summary>Accounting.Reverse swaps the posting, voids the original and does it once.</summary>
[Collection("Sequential")]
public class ReverseActionTests(IntegrationTestFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(string Cash, string Income, string Mapping)> MappingAsync()
    {
        var cash = await AccountingTestData.AccountAsync(fixture, "1010", AccountType.Asset);
        var income = await AccountingTestData.AccountAsync(fixture, "4010", AccountType.Income);
        var name = $"map-{Guid.NewGuid():N}"[..14];
        using var scope = fixture.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new AccountMapping
        {
            Name = name,
            Rule = new MappingRule([new(cash, "debit", "{{Data.Total}}"), new(income, "credit", "{{Data.Total}}")], true, null),
        });
        await session.SaveChangesAsync(Ct);
        return (cash, income, name);
    }

    private static Content Subject(decimal total) => new()
    {
        Id = Guid.NewGuid(),
        ContentType = "receipt",
        Data = new Dictionary<string, object> { ["Total"] = total },
    };

    private async Task<WorkflowActionResult> RunAsync<TAction>(Content subject, Dictionary<string, string> parameters)
        where TAction : class, IWorkflowAction
    {
        parameters["Date"] = "2026-10-05";
        parameters["RunId"] = Guid.NewGuid().ToString();
        using var scope = fixture.Services.CreateScope();
        IWorkflowAction action = ActivatorUtilities.CreateInstance<TAction>(scope.ServiceProvider);
        return await action.RunAsync(new WorkflowContext { Parameters = parameters, Content = subject, TriggerEvent = "Updated" }, Ct);
    }

    private Task<WorkflowActionResult> PostAsync(Content subject, string mapping) =>
        RunAsync<PostJournalAction>(subject, new() { ["Mapping"] = mapping, ["ActionKey"] = "receipt" });

    private Task<WorkflowActionResult> ReverseAsync(Content subject, string? reason = "receipt cancelled") =>
        RunAsync<ReverseAction>(subject, reason is null
            ? new() { ["ActionKey"] = "receipt" }
            : new() { ["ActionKey"] = "receipt", ["Reason"] = reason });

    [Fact]
    public async Task A_posted_receipt_is_reversed_with_swapped_lines_and_the_original_is_voided()
    {
        var (cash, income, mapping) = await MappingAsync();
        var subject = Subject(640m);
        await PostAsync(subject, mapping);

        var result = await ReverseAsync(subject);

        result.Succeeded.Should().BeTrue(result.Error);
        var entries = await AccountingTestData.EntriesAsync(fixture, $"{subject.Id:N}:");
        var original = entries.Single(e => !AccountingTestData.Text(e, "Reference").EndsWith(":reverse", StringComparison.Ordinal));
        var reversal = entries.Single(e => AccountingTestData.Text(e, "Reference").EndsWith(":reverse", StringComparison.Ordinal));
        AccountingTestData.Lines(reversal).Should().Equal((cash, 0m, 640m), (income, 640m, 0m));
        AccountingTestData.Text(reversal, "VoidsEntryId").Should().Be(original.Id.ToString());
        result.Outputs["id"].Should().Be(reversal.Id.ToString());

        var voided = await AccountingTestData.LoadAsync(fixture, original.Id);
        voided.LifecycleState.Should().Be("Voided");
        AccountingTestData.Text(voided, "VoidReason").Should().Be("receipt cancelled");
    }

    [Fact]
    public async Task Running_it_twice_yields_one_reversal()
    {
        var (_, _, mapping) = await MappingAsync();
        var subject = Subject(120m);
        await PostAsync(subject, mapping);

        var first = await ReverseAsync(subject);
        var second = await ReverseAsync(subject);

        second.Succeeded.Should().BeTrue(second.Error);
        second.Outputs["id"].Should().Be(first.Outputs["id"]);
        (await AccountingTestData.EntriesAsync(fixture, $"{subject.Id:N}:")).Should().HaveCount(2);
    }

    [Fact]
    public async Task Reversing_without_a_posting_is_a_permanent_failure()
    {
        var result = await ReverseAsync(Subject(10m));

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Be("No posting to reverse.");
    }

    [Fact]
    public async Task A_missing_reason_is_a_permanent_failure_and_changes_nothing()
    {
        var (_, _, mapping) = await MappingAsync();
        var subject = Subject(10m);
        await PostAsync(subject, mapping);

        var result = await ReverseAsync(subject, reason: null);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        (await AccountingTestData.EntriesAsync(fixture, $"{subject.Id:N}:")).Should().ContainSingle();
    }

    [Fact]
    public async Task After_the_reversal_the_accounts_are_back_to_zero()
    {
        var (cash, income, mapping) = await MappingAsync();
        var subject = Subject(500m);
        await PostAsync(subject, mapping);
        using (var scope = fixture.Services.CreateScope())
        {
            var balances = await scope.ServiceProvider.GetRequiredService<ReportingService>().BalancesAsync(null, Ct);
            balances.Single(b => b.Code == cash).Balance.Should().Be(500m);
        }

        (await ReverseAsync(subject)).Succeeded.Should().BeTrue();

        using var after = fixture.Services.CreateScope();
        var later = await after.ServiceProvider.GetRequiredService<ReportingService>().BalancesAsync(null, Ct);
        later.Where(b => b.Code == cash || b.Code == income).Select(b => b.Balance).Should().OnlyContain(b => b == 0m);
    }
}
