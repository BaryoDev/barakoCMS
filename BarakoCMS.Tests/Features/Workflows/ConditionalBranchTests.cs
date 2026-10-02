using System.Text.Json;
using barakoCMS.Features.Workflows;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// Issue #1050: a Conditional run by the runner takes the branch its condition names.
/// </summary>
/// <remarks>
/// Every test queues a run and lets <see cref="WorkflowRunner"/> execute it, because the defect was
/// in what the runner hands the action, and calling the action directly never showed it. Each branch
/// holds one Email to its own address, so the fixture's recording transport says which branch ran.
/// </remarks>
[Collection("Sequential")]
public class ConditionalBranchTests
{
    private readonly IntegrationTestFixture _fixture;

    public ConditionalBranchTests(IntegrationTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_published_entry_runs_the_then_branch_of_a_status_condition()
    {
        var (then, otherwise) = (Address("then"), Address("else"));

        await RunConditionalAsync(
            Conditional("{{status}} == Published", EmailTo(then), EmailTo(otherwise)),
            ContentStatus.Published);

        ShouldHaveRunOnly(then, then, otherwise);
    }

    /// <summary>The same condition on an entry it does not hold for. Passes with or without the fix.</summary>
    [Fact]
    public async Task A_draft_entry_runs_the_else_branch_of_the_same_condition()
    {
        var (then, otherwise) = (Address("then"), Address("else"));

        await RunConditionalAsync(
            Conditional("{{status}} == Published", EmailTo(then), EmailTo(otherwise)),
            ContentStatus.Draft);

        ShouldHaveRunOnly(otherwise, then, otherwise);
    }

    [Fact]
    public async Task A_not_equal_condition_that_does_not_hold_runs_the_else_branch()
    {
        var (then, otherwise) = (Address("then"), Address("else"));

        await RunConditionalAsync(
            Conditional("{{contentType}} != Post", EmailTo(then), EmailTo(otherwise)),
            ContentStatus.Published,
            contentType: "Post");

        ShouldHaveRunOnly(otherwise, then, otherwise);
    }

    [Fact]
    public async Task A_data_field_equal_to_the_quoted_value_runs_the_then_branch()
    {
        var (then, otherwise) = (Address("then"), Address("else"));

        await RunConditionalAsync(
            Conditional("{{data.Plan}} == \"Paid\"", EmailTo(then), EmailTo(otherwise)),
            ContentStatus.Draft,
            new() { ["Plan"] = "Paid" });

        ShouldHaveRunOnly(then, then, otherwise);
    }

    [Fact]
    public async Task A_data_field_holding_a_value_is_not_equal_to_an_empty_string()
    {
        var (then, otherwise) = (Address("then"), Address("else"));

        await RunConditionalAsync(
            Conditional("{{data.Plan}} == \"\"", EmailTo(then), EmailTo(otherwise)),
            ContentStatus.Draft,
            new() { ["Plan"] = "Paid" });

        ShouldHaveRunOnly(otherwise, then, otherwise);
    }

    /// <summary>
    /// A value is compared, never parsed: one holding an operator used to split the condition into
    /// three parts, which the evaluator answers false for whatever was asked.
    /// </summary>
    [Fact]
    public async Task A_data_field_holding_an_operator_is_compared_as_a_value()
    {
        var (then, otherwise) = (Address("then"), Address("else"));

        await RunConditionalAsync(
            Conditional("{{data.Note}} != Paid", EmailTo(then), EmailTo(otherwise)),
            ContentStatus.Draft,
            new() { ["Note"] = "a == b" });

        ShouldHaveRunOnly(then, then, otherwise);
    }

    [Fact]
    public async Task A_nested_conditional_runs_its_own_then_branch_inside_the_parents_then_branch()
    {
        var (innerThen, innerElse, outerElse) = (Address("inner-then"), Address("inner-else"), Address("outer-else"));

        var inner = Children("Conditional",
            Conditional("{{data.Plan}} == \"Paid\"", EmailTo(innerThen), EmailTo(innerElse)));

        await RunConditionalAsync(
            Conditional("{{status}} == Published", inner, EmailTo(outerElse)),
            ContentStatus.Published,
            new() { ["Plan"] = "Paid" });

        ShouldHaveRunOnly(innerThen, innerThen, innerElse, outerElse);
    }

    /// <summary>
    /// The parent's condition here is one the runner answered correctly even before the fix (a
    /// not-equal against a different value), so the only thing that can send this to the wrong
    /// address is the parent resolving the child's condition before the child evaluates it.
    /// </summary>
    [Fact]
    public async Task A_parent_conditional_hands_its_child_the_condition_as_written()
    {
        var (innerThen, innerElse, outerElse) = (Address("inner-then"), Address("inner-else"), Address("outer-else"));

        var inner = Children("Conditional",
            Conditional("{{data.Plan}} == \"Paid\"", EmailTo(innerThen), EmailTo(innerElse)));

        await RunConditionalAsync(
            Conditional("{{status}} != Draft", inner, EmailTo(outerElse)),
            ContentStatus.Published,
            new() { ["Plan"] = "Paid" });

        ShouldHaveRunOnly(innerThen, innerThen, innerElse, outerElse);
    }

    private static string Address(string branch) => $"{branch}-{Guid.NewGuid():N}@example.com";

    private static Dictionary<string, string> Conditional(string condition, string thenActions, string elseActions) =>
        new() { ["Condition"] = condition, ["ThenActions"] = thenActions, ["ElseActions"] = elseActions };

    private static string Children(string type, Dictionary<string, string> parameters) =>
        JsonSerializer.Serialize(new[] { new { Type = type, Parameters = parameters } });

    private static string EmailTo(string to) =>
        Children("Email", new() { ["To"] = to, ["Subject"] = "s", ["Body"] = "b" });

    /// <summary>
    /// Exactly one message across every address the workflow could have sent to, and it went to
    /// <paramref name="expected"/>. The count comes first so the address check has something to run on.
    /// </summary>
    private void ShouldHaveRunOnly(string expected, params string[] everyBranch)
    {
        var sent = _fixture.Email.Messages.Where(m => everyBranch.Contains(m.To)).ToList();

        sent.Should().HaveCount(1, "one branch runs, and each branch sends one email");
        sent[0].To.Should().Be(expected);
    }

    private async Task RunConditionalAsync(
        Dictionary<string, string> parameters,
        ContentStatus status,
        Dictionary<string, object>? data = null,
        string contentType = "article")
    {
        var ct = TestContext.Current.CancellationToken;
        var store = _fixture.Services.GetRequiredService<IDocumentStore>();
        var contentId = Guid.NewGuid();
        var runId = Guid.NewGuid();

        await using (var session = store.LightweightSession())
        {
            session.Store(new Content
            {
                Id = contentId,
                ContentType = contentType,
                Status = status,
                Data = data ?? new Dictionary<string, object>(),
            });

            var run = new WorkflowRun
            {
                Id = runId,
                WorkflowDefinitionId = Guid.NewGuid(),
                WorkflowName = "Conditional through the runner",
                // The runner looks only at the 20 oldest unfinished runs, so other tests' runs waiting
                // on a retry can hide a new one for the whole loop (#695). Oldest means it is seen.
                CreatedAt = DateTimeOffset.UnixEpoch,
                ContentId = contentId,
                ContentType = contentType,
                TriggerEvent = "Published",
                TriggeringEventSequence = 1,
                Actions =
                [
                    new WorkflowActionAttempt
                    {
                        Ordinal = 0,
                        ActionType = "Conditional",
                        IdempotencyKey = $"{Guid.NewGuid():N}",
                        Parameters = parameters,
                    },
                ],
            };
            run.Recompute();
            session.Store(run);
            await session.SaveChangesAsync(ct);
        }

        // Either this runner or the fixture's hosted one may claim it. Both run on the fixture's
        // services, so wait for the attempt to finish rather than for a particular runner.
        var runner = new WorkflowRunner(
            _fixture.Services,
            _fixture.Services.GetRequiredService<ILogger<WorkflowRunner>>(),
            _fixture.Services.GetRequiredService<IConfiguration>());

        WorkflowActionAttempt? attempt = null;
        for (var i = 0; i < 200; i++)
        {
            await using (var check = store.QuerySession())
            {
                attempt = (await check.LoadAsync<WorkflowRun>(runId, ct))!.Actions[0];
                if (attempt.Status is AttemptStatus.Succeeded or AttemptStatus.Failed) break;
            }

            if (!await runner.RunOnceAsync(ct))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            }
        }

        attempt.Should().NotBeNull();
        attempt!.Status.Should().Be(AttemptStatus.Succeeded,
            "the conditional and the one child it ran both succeed; error '{0}'", attempt.Error);
    }
}
