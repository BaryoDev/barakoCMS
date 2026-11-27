using barakoCMS.Core.Interfaces;
using barakoCMS.Features.EmailTemplates;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BarakoCMS.Tests.Features.EmailTemplates;

/// <summary>
/// How an Email step reads the datasets a template declares: a read that cannot finish is a retry,
/// all of them share one deadline and the first that fails ends the reading, a loop over a dataset
/// the template never declared is refused, and the app's own wiring reads the saved query.
/// </summary>
[Collection("Integration1")]
public class TemplateDatasetReadTests(IntegrationTestFixture fixture)
{
    private const string TwoLoops = "{{#each lines}}{{row.Qty}},{{/each}}{{#each more}}{{row.Qty}},{{/each}}";

    private const string TwoDatasets = "{\"lines\":\"" + InvoiceKit.QuerySlug + "\",\"more\":\"" + InvoiceKit.QuerySlug + "\"}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Stub(Func<int, QueryRunContext, Task<QueryResult>> run) : IContextQueryRunner
    {
        public List<QueryRunContext> Calls { get; } = [];

        public Task<QueryResult> RunAsync(QueryDefinition definition, QueryRunContext context, CancellationToken ct)
        {
            Calls.Add(context);
            return run(Calls.Count, context);
        }

        public Task<string?> ValidateAsync(QueryDefinition definition, QueryRunContext context, CancellationToken ct) =>
            Task.FromResult<string?>(null);
    }

    private async Task<InvoiceKit> SeededAsync(string template = InvoiceKit.Template, string? datasets = null)
    {
        var kit = new InvoiceKit(fixture);
        await kit.SeedAsync([new("Widget", 1, 1m, 1m)], template: template, datasets: datasets);
        return kit;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_dataset_read_that_times_out_or_throws_fails_the_step_for_a_retry_and_sends_nothing(bool timesOut)
    {
        var kit = await SeededAsync();
        var runner = new Stub((_, _) => timesOut
            ? Task.FromResult(QueryResult.Refused("The query took longer than 15 seconds.") with { TimedOut = true })
            : throw new InvalidOperationException("the database went away"));

        var (result, sent) = await kit.SendAsync(queries: runner);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeTrue("a read that timed out or threw may get through on the next try");
        result.Error.Should().Contain("'lines'");
        sent.Should().BeNull();
        runner.Calls.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_dataset_the_query_refuses_stays_a_permanent_failure()
    {
        var kit = await SeededAsync();
        var runner = new Stub((_, _) => Task.FromResult(QueryResult.Refused("You may not read this.")));

        var (result, sent) = await kit.SendAsync(queries: runner);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        sent.Should().BeNull();
    }

    [Fact]
    public async Task The_first_dataset_that_cannot_be_read_ends_the_reading_and_the_second_is_never_run()
    {
        var kit = await SeededAsync(TwoLoops, TwoDatasets);
        var runner = new Stub((_, _) => Task.FromResult(QueryResult.Refused("You may not read this.")));

        var (result, sent) = await kit.SendAsync(queries: runner);

        result.Succeeded.Should().BeFalse();
        sent.Should().BeNull();
        runner.Calls.Should().ContainSingle("the second dataset is not run once the first was refused");
        runner.Calls[0].Timeout.Should().NotBeNull().And.Subject.Value.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_dataset_skipped_after_the_first_failure_reports_that_failure_when_the_template_reaches_it_first()
    {
        var kit = await SeededAsync("{{#each more}}{{row.Qty}},{{/each}}{{#each lines}}{{row.Qty}},{{/each}}", TwoDatasets);
        var runner = new Stub((_, _) => Task.FromResult(QueryResult.Refused("Ask the owner for read access.")));

        var (result, sent) = await kit.SendAsync(queries: runner);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("'lines'").And.Contain("Ask the owner for read access.").And.NotContain("was not read");
        sent.Should().BeNull();
        runner.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task The_datasets_share_one_deadline_and_the_second_is_given_what_the_first_left()
    {
        var kit = await SeededAsync(TwoLoops, TwoDatasets);
        var runner = new Stub(async (_, _) =>
        {
            await Task.Delay(60, Ct);
            return new QueryResult([], null);
        });

        var (result, _) = await kit.SendAsync(queries: runner);

        result.Succeeded.Should().BeTrue(result.Error);
        runner.Calls.Should().HaveCount(2);
        runner.Calls[0].Timeout!.Value.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
        runner.Calls[1].Timeout!.Value.Should().BeLessThan(runner.Calls[0].Timeout!.Value, "the first read used some of the 30 seconds");
    }

    [Fact]
    public async Task A_loop_over_a_dataset_the_template_does_not_declare_is_refused_by_name_and_sends_nothing()
    {
        var kit = await SeededAsync("{{#each items}}{{row.Qty}}{{/each}}");
        var runner = new Stub((_, _) => Task.FromResult(new QueryResult([], null)));

        var (result, sent) = await kit.SendAsync(queries: runner);

        result.Succeeded.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Error.Should().Contain("'items'").And.Contain("does not declare");
        sent.Should().BeNull();
        runner.Calls.Should().BeEmpty("the template is refused before any dataset is read");
    }

    [Fact]
    public async Task A_dataset_name_of_five_thousand_characters_is_not_echoed_whole_by_a_send()
    {
        var name = new string('n', 5_000);
        var kit = await SeededAsync("{{#each " + name + "}}{{row.Qty}}{{/each}}", "{\"" + name + "\":\"" + InvoiceKit.QuerySlug + "\"}");

        var (result, sent) = await kit.SendAsync();

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain(new string('n', 40) + "...").And.NotContain(new string('n', 41));
        sent.Should().BeNull();
    }

    [Fact]
    public async Task The_apps_own_extractor_reads_a_declared_dataset_through_the_saved_query()
    {
        var kit = new InvoiceKit(fixture);
        await kit.SeedAsync([new("Widget", 2, 10m, 20m), new("Gadget", 3, 4m, 12m)]);

        using var scope = fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Slug = kit.Tenant;
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var extractor = scope.ServiceProvider.GetRequiredService<ITemplateVariableExtractor>();
        var entry = (await session.LoadAsync<Content>(kit.InvoiceId, Ct))!;
        await extractor.PrepareAsync(entry, WorkflowEvents.Created, 0, [], Ct);

        var recorder = new RecordingEmailService();
        var action = new EmailAction(recorder, NullLogger<EmailAction>.Instance, session: session, extractor: extractor);
        var result = await action.RunAsync(new() { ["To"] = "customer@example.com", ["Template"] = kit.TemplateSlug }, entry, Ct);

        result.Succeeded.Should().BeTrue(result.Error);
        recorder.Messages.Should().ContainSingle().Which.Body.Should().Contain("Widget").And.Contain("Gadget");
    }
}
