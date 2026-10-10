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
/// A template's cost is bounded: resolving and checking one is linear in its length, the engine
/// sends a parameter past <see cref="TemplateExpression.MaxTemplateLength"/> as written, and saving
/// a workflow warns about one without refusing it.
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
    /// rest of the template from every unclosed opening marker. Fifteen thousand markers keep the
    /// template under the cap, so the pairing itself is what is timed.
    /// </summary>
    [Fact]
    public void Fifteen_thousand_unclosed_loop_markers_under_the_cap_resolve_quickly_and_as_written()
    {
        var unclosed = string.Concat(Enumerable.Repeat("{{#each data.A}}x", 15_000));
        unclosed.Length.Should().BeLessThan(TemplateExpression.MaxTemplateLength - 9);
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

    /// <summary>
    /// Red without the render-time cap: sixty thousand markers, past the cap, are not resolved at
    /// all. The hole in front would otherwise be filled. The run gets a note saying why.
    /// </summary>
    [Fact]
    public void A_parameter_past_the_cap_is_sent_as_written_with_a_note()
    {
        var template = "{{data.Title}}" + string.Concat(Enumerable.Repeat("{{#each data.A}}x", 60_000));
        template.Length.Should().BeGreaterThan(TemplateExpression.MaxTemplateLength);

        var entry = new Content { Id = Guid.NewGuid(), ContentType = "batch", Data = new() { ["Title"] = "B1" } };
        var context = TemplateContext.Unprepared with { Notes = [] };

        var timer = Stopwatch.StartNew();
        var resolved = TemplateVariableExtractor.Resolve(template, entry, TemplateValueEncoding.Html, context);
        timer.Stop();

        resolved.Should().Be(template);
        context.Notes.Should().HaveCount(1);
        context.Notes![0].Should().Be(
            $"A parameter of {template.Length} characters is past the {TemplateExpression.MaxTemplateLength} character cap, "
          + "so its placeholders were not resolved and it was sent as written.");
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

    /// <summary>
    /// Red before the cap warning: nothing was said. The save is never refused, since a workflow
    /// saved before the cap must still save.
    /// </summary>
    [Fact]
    public void A_parameter_longer_than_the_cap_is_warned_about_by_name_and_one_at_the_cap_is_not()
    {
        var atCap = new string('a', TemplateExpression.MaxTemplateLength);

        var over = _validator.Validate(Workflow(atCap + "a"));
        var at = _validator.Validate(Workflow(atCap));

        over.IsValid.Should().BeTrue(string.Join("; ", over.Errors.Select(e => e.Message)));
        over.Errors.Should().BeEmpty();
        over.Warnings.Should().HaveCount(1);
        over.Warnings[0].Field.Should().Be("actions[0].parameters.Body");
        over.Warnings[0].Message.Should().Contain($"Past {TemplateExpression.MaxTemplateLength}");

        at.IsValid.Should().BeTrue(string.Join("; ", at.Errors.Select(e => e.Message)));
        at.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Saving_a_workflow_with_a_parameter_past_the_cap_saves_it_with_a_warning_naming_the_parameter()
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

        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "got {0}", text.Length <= 500 ? text : text[..500]);

        using var doc = System.Text.Json.JsonDocument.Parse(text);
        doc.RootElement.GetProperty("id").GetGuid().Should().NotBeEmpty();
        var warnings = doc.RootElement.GetProperty("warnings").EnumerateArray()
            .Where(w => w.GetProperty("message").GetString()!.Contains($"Past {TemplateExpression.MaxTemplateLength}"))
            .ToList();
        warnings.Should().HaveCount(1);
        warnings[0].GetProperty("field").GetString().Should().Be("actions[0].parameters.Body");
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
