using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Moq;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// A template's cost is bounded: resolving and checking one is linear in its length, and a workflow
/// is not saved with a parameter longer than <see cref="TemplateExpression.MaxTemplateLength"/>.
/// </summary>
[Collection("Sequential")]
public class WorkflowTemplateBoundsTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;
    private readonly WorkflowSchemaValidator _validator;

    public WorkflowTemplateBoundsTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.CreateClient();

        var registry = new Mock<IWorkflowPluginRegistry>();
        registry.Setup(r => r.IsActionRegistered(It.IsAny<string>())).Returns(true);
        _validator = new WorkflowSchemaValidator(registry.Object, Mock.Of<Marten.IQuerySession>());
    }

    public async ValueTask InitializeAsync() =>
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", await _fixture.StoredUserTokenAsync("SuperAdmin"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Red before the review fix: a loop was paired by a pattern with a lazy body, which scanned the
    /// rest of the template from every unclosed opening marker, so this took minutes.
    /// </summary>
    [Fact]
    public void Sixty_thousand_unclosed_loop_markers_resolve_quickly_and_as_written()
    {
        var unclosed = string.Concat(Enumerable.Repeat("{{#each data.A}}x", 60_000));
        var closedOnce = unclosed + "{{/each}}";

        var entry = new Content { Id = Guid.NewGuid(), ContentType = "batch", Data = new() { ["A"] = "x" } };
        var context = TemplateContext.Unprepared with
        {
            References = new Dictionary<string, TemplateFollowed> { ["A"] = new(true, 1, [entry]) },
            Notes = [],
        };

        var timer = Stopwatch.StartNew();
        var first = TemplateVariableExtractor.Resolve(unclosed, entry, TemplateValueEncoding.Html, context);
        var second = TemplateVariableExtractor.Resolve(closedOnce, entry, TemplateValueEncoding.Html, context);
        var problems = TemplateExpression.Problems(closedOnce, onTransition: false).ToList();
        timer.Stop();

        first.Should().Be(unclosed);
        second.Should().Be(closedOnce, "the one loop holds another opening marker, so it is sent as written");
        problems.Should().HaveCount(1);
        problems[0].Should().Be("The loop over 'data.A' holds another loop, and loops do not nest, so it is sent as written.");
        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    /// <summary>A guard that passes both ways: one closed loop between two holes still renders.</summary>
    [Fact]
    public void A_closed_loop_between_holes_renders_each_item()
    {
        var one = new Content { Id = Guid.NewGuid(), ContentType = "runner", Data = new() { ["Name"] = "Ana" } };
        var two = new Content { Id = Guid.NewGuid(), ContentType = "runner", Data = new() { ["Name"] = "Ben" } };
        var entry = new Content { Id = Guid.NewGuid(), ContentType = "batch", Data = new() { ["Title"] = "B1" } };
        var context = TemplateContext.Unprepared with
        {
            References = new Dictionary<string, TemplateFollowed> { ["Runners"] = new(true, 2, [one, two]) },
        };

        TemplateVariableExtractor.Resolve(
                "{{data.Title}}: {{ #each data.Runners }}{{data.Name}};{{ /each }} {{/each}}", entry, TemplateValueEncoding.None, context)
            .Should().Be("B1: Ana;Ben; {{/each}}");
    }

    /// <summary>Red before the cap: a parameter of any length was saved.</summary>
    [Fact]
    public void A_parameter_longer_than_the_cap_is_refused_naming_it_and_one_at_the_cap_is_not()
    {
        var atCap = new string('a', TemplateExpression.MaxTemplateLength);

        var refused = _validator.Validate(Workflow(atCap + "a"));
        var accepted = _validator.Validate(Workflow(atCap));

        refused.IsValid.Should().BeFalse();
        refused.Errors.Should().HaveCount(1);
        refused.Errors[0].Field.Should().Be("actions[0].parameters.Body");
        refused.Errors[0].Message.Should().Contain($"at most {TemplateExpression.MaxTemplateLength}");

        accepted.IsValid.Should().BeTrue(string.Join("; ", accepted.Errors.Select(e => e.Message)));
    }

    [Fact]
    public async Task Saving_a_workflow_with_a_parameter_past_the_cap_answers_400_naming_the_parameter()
    {
        var type = $"cap{Guid.NewGuid():N}"[..16];

        var response = await _client.PostAsJsonAsync("/api/workflows", new
        {
            name = $"too long {type}",
            triggerContentType = type,
            triggerEvent = "Created",
            actions = new[] { new { type = "Email", parameters = new Dictionary<string, string>
            {
                ["To"] = "a@example.com", ["Subject"] = "s", ["Body"] = new string('a', TemplateExpression.MaxTemplateLength + 1),
            } } },
        }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("actions[0].parameters.Body");
    }

    private static WorkflowDefinition Workflow(string body) => new()
    {
        Name = "bounds",
        TriggerContentType = "timeEntry",
        TriggerEvent = "Created",
        Actions =
        [
            new WorkflowAction
            {
                Type = "Email",
                Parameters = new() { ["To"] = "a@example.com", ["Subject"] = "s", ["Body"] = body },
            },
        ],
    };
}
