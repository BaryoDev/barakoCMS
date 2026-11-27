using BarakoCMS.Tests.Features.Workflows;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using static BarakoCMS.Tests.Features.Connectors.QueryKit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// The Query step: it reads the entries a saved query finds as the user behind the event and hands
/// the first row's fields to the steps after it.
/// </summary>
/// <remarks>
/// The step after the query is the test runner action that records the parameters it was given, in
/// place of an Email step. What the Email step would send to is exactly the resolved parameter.
/// </remarks>
[Collection("Integration1")]
public class QueryActionTests(IntegrationTestFixture factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> ActorAsync(QueryFlow flow, params string[]? readable) =>
        await QueryKit.ActorAsync(flow.Store, Ct, (flow.Students, readable));

    [Fact]
    public async Task An_enrollment_finds_the_student_and_a_later_step_sends_to_the_students_email()
    {
        var flow = new QueryFlow(factory);
        await flow.SeedTypesAsync(null,
            flow.Student("S-1", "one@example.com", "One"), flow.Student("S-2", "two@example.com", "Two"));
        await flow.SaveQueryAsync();
        var actor = await ActorAsync(flow);
        var enrollment = await flow.EnrollAsync("S-2", actor);

        var run = await flow.RunAsync(enrollment, flow.QueryStep("Email", "Name"), QueryFlow.Then());

        run.Actions.Select(a => a.Status).Should().Equal(AttemptStatus.Succeeded, AttemptStatus.Succeeded);
        QueryFlow.SentTo(run, 1).Should().Be("two@example.com");
        run.Actions[0].Outputs.Should().Contain("Name", "Two");
    }

    [Fact]
    public async Task No_match_records_a_note_and_hands_on_empty_outputs()
    {
        var flow = new QueryFlow(factory);
        await flow.SeedTypesAsync(null, flow.Student("S-1", "one@example.com", "One"));
        await flow.SaveQueryAsync();
        var enrollment = await flow.EnrollAsync("S-404", await ActorAsync(flow));

        var run = await flow.RunAsync(enrollment, flow.QueryStep("Email"), QueryFlow.Then());

        run.Actions[0].Status.Should().Be(AttemptStatus.Succeeded);
        run.Actions[0].Error.Should().Contain("returned no entries");
        run.Actions[0].Outputs!["Email"].Should().BeEmpty();
        QueryFlow.SentTo(run, 1).Should().BeEmpty();
    }

    [Fact]
    public async Task Two_matches_with_limit_one_keep_the_first_by_order_and_the_id_breaks_a_tie()
    {
        var flow = new QueryFlow(factory);
        var at = DateTime.UtcNow.AddMinutes(-5);
        var stem = Guid.NewGuid().ToString("n")[..30];
        var lower = Guid.Parse(stem + "01");
        var higher = Guid.Parse(stem + "02");

        // Same number, same creation time. The higher id is stored first, so the order of storage
        // is not what picks the row.
        await flow.SeedTypesAsync(null,
            flow.Student("S-9", "higher@example.com", "Higher", at, higher),
            flow.Student("S-9", "lower@example.com", "Lower", at, lower));
        await flow.SaveQueryAsync(sort: "createdAt");
        var enrollment = await flow.EnrollAsync("S-9", await ActorAsync(flow));

        var run = await flow.RunAsync(enrollment, flow.QueryStep("Email"), QueryFlow.Then());

        run.Actions[0].Status.Should().Be(AttemptStatus.Succeeded, run.Actions[0].Error);
        QueryFlow.SentTo(run, 1).Should().Be("lower@example.com");
        // The limit is one, so one row came back. The note counts rows returned, not rows that matched.
        run.Actions[0].Error.Should().Contain("returned 1 entry").And.NotContain("first row was kept");
    }

    [Fact]
    public async Task An_aggregate_query_is_refused_with_the_message_the_other_readers_give()
    {
        var flow = new QueryFlow(factory);
        await flow.SeedTypesAsync(null, flow.Student("S-1", "one@example.com", "One"));
        await flow.SaveQueryAsync(shape: q =>
        {
            q.Fields = [];
            q.Limit = QueryDefinition.DefaultLimit;
            q.Measures = [new QueryMeasure { Fn = "count", As = "n" }];
        });
        var enrollment = await flow.EnrollAsync("S-1", await ActorAsync(flow));

        var run = await flow.RunAsync(enrollment, flow.QueryStep("n"), QueryFlow.Then());

        run.Actions[0].Status.Should().Be(AttemptStatus.Failed);
        run.Actions[0].Error.Should().Contain("aggregate query computes figures for a signed-in reader and cannot feed a workflow");
    }

    [Fact]
    public async Task A_value_over_the_output_cap_fails_the_step_and_is_not_cut()
    {
        var flow = new QueryFlow(factory);
        var long_ = new string('x', ActionOutputs.MaxValueLength + 1);
        await flow.SeedTypesAsync(null, flow.Student("S-1", long_, "One"));
        await flow.SaveQueryAsync();
        var enrollment = await flow.EnrollAsync("S-1", await ActorAsync(flow));

        var run = await flow.RunAsync(enrollment, flow.QueryStep("Email"), QueryFlow.Then());

        run.Actions[0].Status.Should().Be(AttemptStatus.Failed);
        run.Actions[0].Error.Should().Contain("Email").And.Contain(ActionOutputs.MaxValueLength.ToString());
        run.Actions[0].Outputs.Should().BeNullOrEmpty();
        run.Actions[1].Status.Should().NotBe(AttemptStatus.Succeeded, "the step after it has nothing to read");
    }

    [Fact]
    public async Task A_user_whose_rights_were_narrowed_after_the_workflow_was_saved_fails_the_run_and_nothing_is_dropped()
    {
        var flow = new QueryFlow(factory);
        await flow.SeedTypesAsync(null, flow.Student("S-1", "one@example.com", "One"));
        await flow.SaveQueryAsync();
        // Reads Name and nothing else: the query selects Email.
        var enrollment = await flow.EnrollAsync("S-1", await ActorAsync(flow, "Name"));

        var run = await flow.RunAsync(enrollment, flow.QueryStep("Email", "Name"), QueryFlow.Then());

        run.Actions[0].Status.Should().Be(AttemptStatus.Failed);
        run.Actions[0].Error.Should().Contain("Email");
        run.Actions[0].Outputs.Should().BeNullOrEmpty();
        RecordingRunnerAction.Seen.Should().NotContainKey(run.Actions[1].IdempotencyKey);
    }

    [Fact]
    public async Task With_nobody_behind_the_event_only_public_fields_are_read()
    {
        var flow = new QueryFlow(factory);
        await flow.SeedTypesAsync(Field("Email", "text", SensitivityLevel.Sensitive), flow.Student("S-1", "one@example.com", "One"));
        await flow.SaveQueryAsync(select: "Name");
        var anonymous = await flow.EnrollAsync("S-1", null);

        var publicOnly = await flow.RunAsync(anonymous, flow.QueryStep("Name"), QueryFlow.Then("{{actions.0.Name}}"));
        publicOnly.Actions[0].Status.Should().Be(AttemptStatus.Succeeded, publicOnly.Actions[0].Error);
        QueryFlow.SentTo(publicOnly, 1).Should().Be("One");

        await flow.SaveQueryAsync(select: "Email");
        var sensitive = await flow.RunAsync(anonymous, flow.QueryStep("Email"), QueryFlow.Then());
        sensitive.Actions[0].Status.Should().Be(AttemptStatus.Failed);
        sensitive.Actions[0].Error.Should().Contain("Email");
    }

    [Fact]
    public async Task The_query_name_cannot_be_chosen_by_an_entry()
    {
        var flow = new QueryFlow(factory);
        await flow.SeedTypesAsync(null, flow.Student("S-1", "one@example.com", "One"));
        await flow.SaveQueryAsync();
        var enrollment = await flow.EnrollAsync("S-1", await ActorAsync(flow));

        var step = flow.QueryStep("Email");
        step.Parameters["Query"] = "{{data.StudentNo}}";
        var run = await flow.RunAsync(enrollment, step);

        run.Actions[0].Status.Should().Be(AttemptStatus.Failed);
        run.Actions[0].Error.Should().Contain("not a saved query name");
    }

    [Fact]
    public async Task A_step_that_was_not_given_the_tenant_of_its_run_reads_nothing()
    {
        var flow = new QueryFlow(factory);
        await flow.SeedTypesAsync(null, flow.Student("S-1", "one@example.com", "One"));
        await flow.SaveQueryAsync();

        using var scope = factory.Services.CreateScope();
        var action = ActivatorUtilities.CreateInstance<QueryAction>(scope.ServiceProvider);
        var result = await action.RunAsync(new WorkflowContext
        {
            Parameters = new() { ["Query"] = flow.QuerySlug },
            Content = Entry(flow.Enrollments, new() { ["StudentNo"] = "S-1" }),
            TriggerEvent = WorkflowEvents.Created,
            TenantId = "some-other-tenant",
        }, Ct);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("tenant");
        result.Outputs.Should().BeNullOrEmpty();
    }
}
