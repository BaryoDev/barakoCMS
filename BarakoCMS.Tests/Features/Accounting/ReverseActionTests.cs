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

    [Fact]
    public async Task A_reason_naming_a_sensitive_field_fails_the_action()
    {
        var (_, _, mapping) = await MappingAsync();
        var type = "rcpt" + Guid.NewGuid().ToString("N")[..8];
        using (var scope = fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            session.Store(new ContentTypeDefinition
            {
                Id = Guid.NewGuid(),
                Name = type,
                DisplayName = type,
                Fields =
                [
                    new FieldDefinition { Name = "Total", DisplayName = "Total", Type = "decimal" },
                    new FieldDefinition { Name = "Diagnosis", DisplayName = "Diagnosis", Type = "string", Sensitivity = SensitivityLevel.Sensitive },
                ],
            });
            await session.SaveChangesAsync(Ct);
        }

        var subject = new Content
        {
            Id = Guid.NewGuid(),
            ContentType = type,
            Data = new Dictionary<string, object> { ["Total"] = 40m, ["Diagnosis"] = "asthma" },
        };
        (await PostAsync(subject, mapping)).Succeeded.Should().BeTrue();

        var byValue = await ReverseAsync(subject, "cancelled, asthma visit");
        var byName = await ReverseAsync(subject, "cancelled {{Data.Diagnosis}}");

        byValue.Succeeded.Should().BeFalse();
        byValue.Retryable.Should().BeFalse();
        byValue.Error.Should().Contain("Diagnosis");
        byName.Succeeded.Should().BeFalse();
        var original = (await AccountingTestData.EntriesAsync(fixture, $"{subject.Id:N}:")).Single();
        original.LifecycleState.Should().Be("Posted");
        (await ReverseAsync(subject, "cancelled by customer")).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task A_manual_entry_with_VoidsEntryId_is_refused()
    {
        await AccountingTestData.EnsureTypesAsync(fixture);
        var id = Guid.NewGuid();
        var data = new Dictionary<string, object>
        {
            ["Date"] = "2026-10-05",
            ["Lines"] = new List<object>(),
            ["VoidsEntryId"] = id.ToString(),
        };

        using var scope = fixture.Services.CreateScope();
        var errors = await scope.ServiceProvider.GetRequiredService<barakoCMS.Infrastructure.Services.IContentLifecycleRunner>()
            .RunBeforeSaveAsync(AccountingContentTypes.JournalEntry, Guid.NewGuid(), data, null, Guid.NewGuid(), Ct);

        errors.Should().Contain(e => e.Contains("cannot be made to void another"));
    }

    [Fact]
    public async Task An_entry_that_points_at_a_voided_one_still_counts_in_balances()
    {
        var cash = await AccountingTestData.AccountAsync(fixture, "1010", AccountType.Asset);
        var income = await AccountingTestData.AccountAsync(fixture, "4010", AccountType.Income);
        var voidedId = await AccountingTestData.PostAsync(fixture, new DateOnly(2026, 10, 5), cash, income, 30m);
        var otherId = await AccountingTestData.PostAsync(fixture, new DateOnly(2026, 10, 6), cash, income, 70m);
        using (var scope = fixture.Services.CreateScope())
        {
            var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
            var voided = (await session.LoadAsync<Content>(voidedId, Ct))!;
            voided.LifecycleState = "Voided";
            var other = (await session.LoadAsync<Content>(otherId, Ct))!;
            other.Data["VoidsEntryId"] = voidedId.ToString();
            session.Store(voided, other);
            await session.SaveChangesAsync(Ct);
        }

        using var read = fixture.Services.CreateScope();
        var balances = await read.ServiceProvider.GetRequiredService<ReportingService>().BalancesAsync(null, Ct);
        balances.Single(b => b.Code == cash).Balance.Should().Be(70m);
    }
}
