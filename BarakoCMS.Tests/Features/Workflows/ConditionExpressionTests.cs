using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// A Conditional reads its condition as an expression and sees the outputs of the steps before it.
/// The old form, <c>{{x}} == y</c>, keeps its result.
/// </summary>
public class ConditionExpressionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>> NoOutputs =
        new Dictionary<int, IReadOnlyDictionary<string, string>>();

    private static Content Entry() => new()
    {
        Id = Guid.NewGuid(),
        ContentType = "post",
        Status = ContentStatus.Published,
        Data = new Dictionary<string, object>
        {
            ["Priority"] = "High", ["State"] = "Open", ["Name"] = "x", ["Count"] = 5,
        },
    };

    private static ConditionExpression.Verdict Run(string condition, IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>>? outputs = null) =>
        ConditionExpression.Run(ConditionExpression.Prepare(condition, Entry(), outputs ?? NoOutputs), null);

    [Theory]
    [InlineData("{{status}} == Published", true)]
    [InlineData("{{status}} == published", true)]
    [InlineData("{{status}} == \"Published\"", true)]
    [InlineData("{{status}} != Published", false)]
    [InlineData("{{status}} != Draft", true)]
    [InlineData("{{status}} == Nope", false)]
    [InlineData("{{contentType}} == post", true)]
    [InlineData("{{contentType}} == POST", true)]
    [InlineData("{{contentType}} != post", false)]
    [InlineData("{{data.Priority}} == \"High\"", true)]
    [InlineData("{{data.Priority}} == high", true)]
    [InlineData("{{data.Priority}} != High", false)]
    [InlineData("{{data.State}} == Open", true)]
    [InlineData("{{data.State}} == Closed", false)]
    [InlineData("{{data.Name}} == \"x\"", true)]
    [InlineData("{{data.Missing}} == \"\"", true)]
    [InlineData("{{data.Missing}} != x", true)]
    [InlineData("{{data.Count}} == 5", true)]
    [InlineData("{{data.State | nope}} == Open", false)]
    [InlineData("{{createdBy.name}} == \"\"", true)]
    public void An_old_condition_keeps_its_result(string condition, bool expected)
    {
        var verdict = Run(condition);

        verdict.Result.Should().Be(expected);
        verdict.Note.Should().BeNull("the old form is not an evaluation that can fail");
    }

    [Theory]
    [InlineData("status == \"Published\" and data.Priority == \"High\"", true)]
    [InlineData("status == \"Draft\" or data.Priority == \"High\"", true)]
    [InlineData("status == \"Draft\" or data.Priority == \"Low\"", false)]
    [InlineData("data.Count > 3", true)]
    [InlineData("data.Count >= 6", false)]
    [InlineData("{{data.Count}} > 3", true)]
    [InlineData("{{data.Count}} > 3 and {{status}} == \"Published\"", true)]
    [InlineData("not (status == \"Draft\")", true)]
    [InlineData("isblank(data.Missing)", true)]
    [InlineData("data.Count * 2 = 10", true)]
    public void An_expression_gives_its_answer(string condition, bool expected)
    {
        var verdict = Run(condition);

        verdict.Note.Should().BeNull();
        verdict.Result.Should().Be(expected);
    }

    private static Content EntryWith(string field, string value) => new()
    {
        Id = Guid.NewGuid(),
        ContentType = "post",
        Status = ContentStatus.Published,
        Data = new Dictionary<string, object> { [field] = value },
    };

    [Theory]
    [InlineData("Stage", "Not started", "{{data.Stage}} == Not started")]
    [InlineData("Band", "Rock and Roll", "{{data.Band}} == Rock and Roll")]
    [InlineData("Title", "Q1 (draft)", "{{data.Title}} == Q1 (draft)")]
    [InlineData("X", "a=b", "{{data.X}} == a=b")]
    [InlineData("Stage", "NOT STARTED", "{{data.Stage}} == Not started")]
    [InlineData("Stage", "Started", "{{data.Stage}} != Not started")]
    [InlineData("Note", "5 not 6", "{{data.Note}} == 5 not 6")]
    public void An_old_form_value_with_an_operator_word_still_compares(string field, string value, string condition)
    {
        var verdict = ConditionExpression.Run(ConditionExpression.Prepare(condition, EntryWith(field, value), NoOutputs), null);

        verdict.Result.Should().BeTrue();
        verdict.Note.Should().BeNull();
    }

    [Theory]
    [InlineData("Stage", "Done", "{{data.Stage}} == Not started")]
    [InlineData("Band", "Rock", "{{data.Band}} == Rock and Roll")]
    [InlineData("Title", "Q1", "{{data.Title}} == Q1 (draft)")]
    [InlineData("X", "a", "{{data.X}} == a=b")]
    [InlineData("Stage", "Not started", "{{data.Stage}} != Not started")]
    public void An_old_form_value_with_an_operator_word_does_not_match_a_different_value(string field, string value, string condition)
    {
        var verdict = ConditionExpression.Run(ConditionExpression.Prepare(condition, EntryWith(field, value), NoOutputs), null);

        verdict.Result.Should().BeFalse();
    }

    [Theory]
    [InlineData("{{ status }} == \"Draft\"")]
    [InlineData("{{Status}} == Draft")]
    [InlineData("{{createdAt}} < \"2020-01-01\"")]
    [InlineData("{{ createdAt }} < \"2020-01-01\"")]
    [InlineData("Status = \"Draft\"")]
    [InlineData("CreatedAt > \"2020-01-01\"")]
    [InlineData("{{ data.X }} == a")]
    [InlineData("{{ status }} != it's")]
    public void A_condition_that_reads_the_entry_is_seen_as_reading_it_in_any_case_or_spacing(string c)
    {
        ConditionExpression.ReadsTheEntry(c).Should().BeTrue();
    }

    private static IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>> Outputs(params (string Name, string Value)[] values) =>
        new Dictionary<int, IReadOnlyDictionary<string, string>>
        {
            [1] = values.ToDictionary(v => v.Name, v => v.Value),
        };

    [Fact]
    public void A_condition_reads_the_output_of_an_earlier_step()
    {
        var outputs = Outputs(("count", "3"), ("Name", "Ann"));

        Run("actions.1.count > 2", outputs).Result.Should().BeTrue();
        Run("{{actions.1.count}} > 5", outputs).Result.Should().BeFalse();
        Run("actions.1.Name == \"Ann\" and actions.1.count = 3", outputs).Result.Should().BeTrue();
    }

    [Fact]
    public void An_output_that_is_missing_is_nothing_and_the_condition_is_false()
    {
        var verdict = Run("actions.4.count > 2", Outputs(("count", "3")));

        verdict.Result.Should().BeFalse();
    }

    [Fact]
    public void An_output_holding_a_quote_or_a_placeholder_is_text_and_never_syntax()
    {
        var outputs = Outputs(("note", "a \" quote and {{data.Priority}} and \\"), ("other", "x\" or true or \""));

        var notSyntax = Run("actions.1.note != \"x\"", outputs);
        notSyntax.Result.Should().BeTrue();
        notSyntax.Note.Should().BeNull();

        Run("actions.1.note == \"High\"", outputs).Result.Should().BeFalse("the placeholder in the output is not filled in");
        Run("contains(actions.1.note, \"{{data.Priority}}\")", outputs).Result.Should().BeTrue("the author's own text in quotes stays as written");
        Run("actions.1.other == \"x\"", outputs).Result.Should().BeFalse("a quote and an or inside an output are not an operator");
        Run("actions.1.other = \"x\\\" or true or \\\"\"", outputs).Result.Should().BeTrue("the whole output is one text");
    }

    [Theory]
    [InlineData("data.Count >")]
    [InlineData("data.Nope.Deeper == 1")]
    [InlineData("nosuchname == 1")]
    [InlineData("data.Count")]
    [InlineData("status ~ 1")]
    [InlineData("data.__v0 == 1")]
    public void A_condition_that_cannot_be_evaluated_is_false_and_says_so_without_a_value(string condition)
    {
        var verdict = Run(condition);

        verdict.Result.Should().BeFalse();
        verdict.Note.Should().NotBeNullOrEmpty();
        verdict.Note.Should().NotContain("High").And.NotContain("Open");
    }

    [Fact]
    public void A_condition_over_the_expression_length_limit_is_false()
    {
        var verdict = Run("data.Count > 1" + string.Concat(Enumerable.Repeat(" and data.Count > 1", 200)));

        verdict.Result.Should().BeFalse();
        verdict.Note.Should().NotBeNullOrEmpty();
    }

    private sealed class Probe(string type) : IWorkflowAction
    {
        public int Calls { get; private set; }

        public string Type => type;

        public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private static async Task<(WorkflowActionResult Result, Probe Then, Probe Else)> RunConditionalAsync(
        string condition, IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>> outputs)
    {
        var then = new Probe("Then");
        var otherwise = new Probe("Otherwise");
        var services = new Mock<IServiceProvider>();
        services.Setup(x => x.GetService(typeof(IEnumerable<IWorkflowAction>))).Returns(new List<IWorkflowAction> { then, otherwise });
        var action = new ConditionalAction(services.Object, NullLogger<ConditionalAction>.Instance);

        var context = new WorkflowContext
        {
            Parameters = new Dictionary<string, string>
            {
                ["Condition"] = condition,
                ["ThenActions"] = JsonSerializer.Serialize(new[] { new { Type = "Then", Parameters = new Dictionary<string, string>() } }),
                ["ElseActions"] = JsonSerializer.Serialize(new[] { new { Type = "Otherwise", Parameters = new Dictionary<string, string>() } }),
            },
            Content = Entry(),
            TriggerEvent = "Created",
            Outputs = outputs,
        };

        return (await action.RunAsync(context, Ct), then, otherwise);
    }

    [Fact]
    public async Task The_step_runs_the_branch_a_prior_output_picks()
    {
        var (result, then, otherwise) = await RunConditionalAsync("actions.1.count > 2 and status == \"Published\"", Outputs(("count", "3")));

        result.Succeeded.Should().BeTrue();
        then.Calls.Should().Be(1);
        otherwise.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_condition_that_cannot_be_evaluated_runs_the_else_branch_and_records_it_on_the_result()
    {
        var (result, then, otherwise) = await RunConditionalAsync("nosuchname == 1", NoOutputs);

        then.Calls.Should().Be(0);
        otherwise.Calls.Should().Be(1);
        result.Note.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task An_erased_entry_still_refuses_a_condition_on_its_data_in_either_form()
    {
        var erased = new ErasedContent(Guid.NewGuid(), "post");
        var action = new ConditionalAction(new Mock<IServiceProvider>().Object, NullLogger<ConditionalAction>.Instance);

        foreach (var condition in new[] { "{{data.Priority}} == High", "data.Priority == \"High\"", "status == \"Published\"", "{{ status }} == \"Draft\"" })
        {
            var result = await action.RunAsync(new Dictionary<string, string> { ["Condition"] = condition, ["ThenActions"] = "[]" }, erased, Ct);
            result.Succeeded.Should().BeFalse(condition);
            result.Retryable.Should().BeFalse(condition);
        }
    }
}
