using BarakoCMS.Accounting;
using BarakoCMS.Accounting.Domain;
using FluentAssertions;
using Xunit;

namespace BarakoCMS.Tests;

public class CashFlowTests
{
    private static readonly string[] Cash = ["1000", "1010"];

    private static JournalEntry Entry(string number, DateOnly date, params (string Code, decimal Debit, decimal Credit)[] lines) =>
        new()
        {
            EntryNumber = number,
            Date = date,
            Lines = lines.Select(l => new JournalLine { AccountCode = l.Code, Debit = l.Debit, Credit = l.Credit }).ToList(),
        };

    [Fact]
    public void A_transfer_between_cash_accounts_is_neither_a_receipt_nor_a_disbursement()
    {
        var d = new DateOnly(2026, 7, 10);
        var entries = new[]
        {
            Entry("JE-1", d, ("4000", 0, 7000.49m), ("1000", 7000.49m, 0)),
            Entry("JE-2", d.AddDays(1), ("1010", 5000, 0), ("1000", 0, 5000)),
            Entry("JE-3", d.AddDays(2), ("1010", 8000, 0), ("1000", 0, 8000)),
        };

        var flow = Accounting.ReportingService.BuildCashFlow(entries, Cash, 2026, 7);

        flow.Receipts.Should().Be(7000.49m);
        flow.Disbursements.Should().Be(0);
        flow.Closing.Should().Be(7000.49m);
        flow.Lines.Should().HaveCount(5);
        flow.Lines.Select(l => l.RunningBalance).Should().Equal(7000.49m, 12000.49m, 7000.49m, 15000.49m, 7000.49m);
    }

    [Fact]
    public void An_entry_that_moves_cash_in_and_pays_an_expense_counts_only_its_net()
    {
        var d = new DateOnly(2026, 7, 10);
        var entries = new[]
        {
            Entry("JE-1", d, ("1000", 1000, 0), ("1010", 0, 400), ("5000", 0, 600)),
        };

        var flow = Accounting.ReportingService.BuildCashFlow(entries, Cash, 2026, 7);

        flow.Receipts.Should().Be(600);
        flow.Disbursements.Should().Be(0);
    }

    [Fact]
    public void A_transfer_before_the_month_leaves_opening_unchanged_and_a_withdrawal_is_a_disbursement()
    {
        var entries = new[]
        {
            Entry("JE-1", new DateOnly(2026, 6, 1), ("1000", 900, 0), ("3000", 0, 900)),
            Entry("JE-2", new DateOnly(2026, 6, 2), ("1010", 400, 0), ("1000", 0, 400)),
            Entry("JE-3", new DateOnly(2026, 7, 3), ("6000", 250, 0), ("1010", 0, 250)),
        };

        var flow = Accounting.ReportingService.BuildCashFlow(entries, Cash, 2026, 7);

        flow.Opening.Should().Be(900);
        flow.Receipts.Should().Be(0);
        flow.Disbursements.Should().Be(250);
        flow.Closing.Should().Be(650);
        flow.Lines.Should().HaveCount(1);
        flow.Lines[0].RunningBalance.Should().Be(650);
    }
}
