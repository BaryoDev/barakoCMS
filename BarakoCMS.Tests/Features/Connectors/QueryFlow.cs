using BarakoCMS.Tests.Features.Workflows;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using static BarakoCMS.Tests.Features.Connectors.QueryKit;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>
/// What the Query step tests share: a student type and an enrollment type in the default tenant, a
/// saved query that finds a student by the enrollment's number, and a run drained with the hosted
/// runner stopped.
/// </summary>
internal sealed class QueryFlow(IntegrationTestFixture factory)
{
    public const string Slug = "student-by-number";

    public WorkflowStopHarness Harness { get; } = new(factory);

    public IDocumentStore Store => factory.Services.GetRequiredService<IDocumentStore>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public string Students { get; } = TypeName();

    public string Enrollments { get; } = TypeName();

    /// <summary>The slug of this flow's saved query, unique to it.</summary>
    public string QuerySlug { get; } = "qf-" + Guid.NewGuid().ToString("n")[..10];

    public async Task SeedTypesAsync(FieldDefinition? email = null, params Content[] students)
    {
        await QueryKit.SeedAsync(
            Store, null,
            Type(Students, Field("Number"), email ?? Field("Email"), Field("Name")),
            students, Ct);

        await using var session = Store.LightweightSession();
        session.Store(Type(Enrollments, Field("StudentNo")));
        await session.SaveChangesAsync(Ct);
    }

    public Content Student(string number, string email, string name, DateTime? createdAt = null, Guid? id = null) =>
        Entry(Students, new() { ["Number"] = number, ["Email"] = email, ["Name"] = name }, createdAt, id: id);

    /// <summary>The enrollment that fires the run, made by the user the step reads as.</summary>
    public async Task<Guid> EnrollAsync(string number, Guid? by)
    {
        var enrollment = Entry(Enrollments, new() { ["StudentNo"] = number }, createdBy: by);
        await using var session = Store.LightweightSession();
        session.Store(enrollment);
        await session.SaveChangesAsync(Ct);
        return enrollment.Id;
    }

    public async Task SaveQueryAsync(string select = "Email, Name", int limit = 1, string? sort = null, Action<QueryDefinition>? shape = null)
    {
        var parsed = barakoCMS.Infrastructure.Connectors.QueryText.Parse(
            $"from {Students} where Number = @data.StudentNo{(sort is null ? "" : " order by " + sort)} select {select} limit {limit}");
        parsed.Ok.Should().BeTrue(parsed.Error);

        await using var session = Store.LightweightSession();

        // A second save under the same slug replaces the first: the slug is unique.
        var existing = await session.Query<QueryDefinition>().FirstOrDefaultAsync(q => q.Slug == QuerySlug, Ct);

        var definition = parsed.Definition!;
        definition.Id = existing?.Id ?? Guid.NewGuid();
        definition.Name = "Student by number";
        definition.Slug = QuerySlug;
        shape?.Invoke(definition);

        session.Store(definition);
        await session.SaveChangesAsync(Ct);
    }

    public WorkflowActionAttempt QueryStep(params string[] capture)
    {
        var attempt = WorkflowStopHarness.Waiting("Query", due: true);
        attempt.Parameters = new() { ["Query"] = QuerySlug };
        attempt.Capture = [.. capture];
        return attempt;
    }

    /// <summary>A step after the query that records what it was given, standing in for an Email step.</summary>
    public static WorkflowActionAttempt Then(string to = "{{actions.0.Email}}")
    {
        var attempt = WorkflowStopHarness.Waiting("RecordingRunner", due: true);
        attempt.Parameters = new() { ["To"] = to };
        return attempt;
    }

    public async Task<WorkflowRun> RunAsync(Guid contentId, params WorkflowActionAttempt[] attempts)
    {
        var workflowId = await Harness.StoreWorkflowAsync();
        WorkflowRun run = null!;

        await Harness.WithHostedRunnerPausedAsync(async () =>
        {
            // No event sequence, which is the engine called in line with the change: the user behind
            // a create is the entry's author. A sequence with no event stored is nobody.
            var seeded = await Harness.SeedRunAsync(workflowId, contentId, attempts, shape: r =>
            {
                r.TriggerEvent = WorkflowEvents.Created;
                r.TriggeringEventSequence = 0;
            });
            await Harness.DrainAsync();
            run = await Harness.LoadRunAsync(seeded.Id);
        });

        return run;
    }

    public static string SentTo(WorkflowRun run, int ordinal) =>
        RecordingRunnerAction.Seen[run.Actions[ordinal].IdempotencyKey].Parameters["To"];
}
