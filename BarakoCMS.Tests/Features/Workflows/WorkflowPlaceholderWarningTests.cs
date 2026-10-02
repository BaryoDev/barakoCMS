using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentAssertions;
using Moq;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// A placeholder the engine will send as written is listed when the workflow is saved, and never
/// refuses the save. A simulation fills the author with a sample and reads no user.
/// </summary>
[Collection("Sequential")]
public class WorkflowPlaceholderWarningTests : IAsyncLifetime
{
    private readonly IntegrationTestFixture _fixture;
    private readonly HttpClient _client;
    private readonly WorkflowSchemaValidator _validator;

    public WorkflowPlaceholderWarningTests(IntegrationTestFixture fixture)
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

    private static WorkflowDefinition Workflow(string triggerEvent, params WorkflowAction[] actions) => new()
    {
        Name = "warnings",
        TriggerContentType = "timeEntry",
        TriggerEvent = triggerEvent,
        Actions = actions.ToList(),
    };

    private static WorkflowAction Email(string to, string subject, string body) => new()
    {
        Type = "Email",
        Parameters = new() { ["To"] = to, ["Subject"] = subject, ["Body"] = body },
    };

    /// <summary>Red without the change: nothing was listed, and the result had no place to list it.</summary>
    [Fact]
    public void A_mistyped_format_and_an_unknown_placeholder_are_listed_with_their_parameter()
    {
        var result = _validator.Validate(Workflow(
            "Created",
            Email("{{createdBy.email}}", "Hours on {{createdAt | dat \"MMM d\"}}", "{{data.Name}} {{creatdAt}}"),
            Email("a@example.com", "{{transition.name}}", "{{createdAt | date \"h:mm tt\"}}")));

        result.IsValid.Should().BeTrue("a warning never refuses the save");
        result.Errors.Should().BeEmpty();

        result.Warnings.Should().HaveCount(3);
        result.Warnings.Select(w => w.Field).Should().Equal(
            "actions[0].parameters.Subject", "actions[0].parameters.Body", "actions[1].parameters.Subject");
        result.Warnings[0].Message.Should().Contain("'dat', which is not a format");
        result.Warnings[1].Message.Should().Contain("'creatdAt', which is not a placeholder");
        result.Warnings[2].Message.Should().Contain("only filled when the trigger is a transition");
    }

    /// <summary>
    /// The control for the test above, in two halves. Templates the engine fills give no warning,
    /// and the transition placeholders stop being one once the trigger is a transition. The first
    /// half alone passes with or without the change, so it is paired with a count that does not.
    /// </summary>
    [Fact]
    public void Placeholders_the_engine_fills_are_not_listed()
    {
        var filled = Email(
            "{{createdBy.email}}",
            "{{transition.name}} by {{transition.by.name}}",
            "{{duration createdAt transition.at}} {{createdAt | date \"h:mm tt\" \"Asia/Manila\"}} {{data.Pay | money \"PHP\"}} {{data.Name | upper}} {{id}}");

        _validator.Validate(Workflow("transition:ClockOut", filled)).Warnings.Should().BeEmpty();

        var onCreated = _validator.Validate(Workflow("Created", filled)).Warnings;
        onCreated.Should().HaveCount(3, "the subject names two transition placeholders and the body one");
        onCreated.Should().OnlyContain(w => w.Message.Contains("only filled when the trigger is a transition"));
    }

    /// <summary>
    /// Red without the change. A Conditional's branches are read as the actions they hold, and its
    /// condition is not read at all, since the action evaluates it itself.
    /// </summary>
    [Fact]
    public void A_placeholder_in_a_conditionals_child_is_listed_under_the_branch()
    {
        var children = JsonSerializer.Serialize(new[]
        {
            new { Type = "Email", Parameters = new Dictionary<string, string> { ["To"] = "a@example.com", ["Body"] = "{{data.Name | shout}}" } },
        });

        var result = _validator.Validate(Workflow("Created", new WorkflowAction
        {
            Type = "Conditional",
            Parameters = new()
            {
                ["Condition"] = "{{data.State | nope}} == Open",
                ["ThenActions"] = children,
                ["ElseActions"] = "not json {{nope}}",
            },
        }));

        result.IsValid.Should().BeTrue();
        result.Warnings.Should().HaveCount(1);
        result.Warnings[0].Field.Should().Be("actions[0].parameters.ThenActions[0].parameters.Body");
        result.Warnings[0].Message.Should().Contain("'shout', which is not a format");
    }

    /// <summary>Red without the change. Past fifty the list stops, and its last entry says so.</summary>
    [Fact]
    public void The_list_stops_at_fifty_and_says_there_are_more()
    {
        var body = string.Join(" ", Enumerable.Range(0, 60).Select(i => $"{{{{nope{i}}}}}"));

        var result = _validator.Validate(Workflow("Created", Email("a@example.com", "s", body), Email("a@example.com", "s", body)));

        result.Warnings.Should().HaveCount(51, "fifty placeholders and the entry that says there are more");
        result.Warnings[^1].Field.Should().Be("actions");
        result.Warnings[^1].Message.Should().StartWith("More than 50 placeholders");
        result.Warnings[49].Message.Should().StartWith("'{{nope49}}'");
    }

    /// <summary>
    /// Red without the change: the response to a save carried no warnings. The clean workflow beside
    /// it is the control that the list is about the template and not always present.
    /// </summary>
    [Fact]
    public async Task Saving_a_workflow_returns_its_placeholder_warnings_and_still_saves_it()
    {
        var type = $"warn{Guid.NewGuid():N}"[..16];

        var warned = await _client.PostAsJsonAsync("/api/workflows", new
        {
            name = $"warned {type}",
            triggerContentType = type,
            triggerEvent = "Created",
            actions = new[] { new { type = "Email", parameters = new Dictionary<string, string>
            {
                ["To"] = "a@example.com", ["Subject"] = "s", ["Body"] = "On {{createdAt | dat \"MMM d\"}}",
            } } },
        }, Ct);

        warned.StatusCode.Should().Be(HttpStatusCode.OK, "got {0}", await warned.Content.ReadAsStringAsync(Ct));
        using (var doc = JsonDocument.Parse(await warned.Content.ReadAsStringAsync(Ct)))
        {
            doc.RootElement.GetProperty("id").GetGuid().Should().NotBeEmpty();

            var warnings = doc.RootElement.GetProperty("warnings").EnumerateArray().ToList();
            warnings.Should().HaveCount(1);
            warnings[0].GetProperty("field").GetString().Should().Be("actions[0].parameters.Body");
            warnings[0].GetProperty("message").GetString().Should().Contain("'dat', which is not a format");
        }

        var clean = await _client.PostAsJsonAsync("/api/workflows", new
        {
            name = $"clean {type}",
            triggerContentType = type,
            triggerEvent = "Created",
            actions = new[] { new { type = "Email", parameters = new Dictionary<string, string>
            {
                ["To"] = "a@example.com", ["Subject"] = "s", ["Body"] = "On {{createdAt | date \"MMM d\"}}",
            } } },
        }, Ct);

        clean.StatusCode.Should().Be(HttpStatusCode.OK, "got {0}", await clean.Content.ReadAsStringAsync(Ct));
        using (var doc = JsonDocument.Parse(await clean.Content.ReadAsStringAsync(Ct)))
        {
            doc.RootElement.GetProperty("warnings").GetArrayLength().Should().Be(0);
        }
    }

    /// <summary>Red without the change: the validate endpoint returned no warnings.</summary>
    [Fact]
    public async Task Validating_a_workflow_returns_its_placeholder_warnings()
    {
        var response = await _client.PostAsJsonAsync("/api/workflows/validate", new
        {
            name = "validate",
            triggerContentType = "timeEntry",
            triggerEvent = "Created",
            actions = new[] { new { type = "Email", parameters = new Dictionary<string, string>
            {
                ["To"] = "a@example.com", ["Subject"] = "s", ["Body"] = "{{hours createdAt}}",
            } } },
        }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        doc.RootElement.GetProperty("isValid").GetBoolean().Should().BeTrue();
        var warnings = doc.RootElement.GetProperty("warnings").EnumerateArray().ToList();
        warnings.Should().HaveCount(1);
        warnings[0].GetProperty("message").GetString().Should().StartWith("'{{hours createdAt}}' is not a placeholder");
    }

    /// <summary>
    /// Red without the change: the author holes were left as written. The sample entry names a real
    /// user as its author, and the preview shows the sample address, not that user's.
    /// </summary>
    [Fact]
    public async Task A_dry_run_fills_the_author_and_the_transition_with_samples()
    {
        var (_, userId) = await TestHelpers.CreateAdminUserAsync(_fixture);
        var updatedAt = new DateTime(2026, 9, 14, 9, 0, 0, DateTimeKind.Utc);

        var response = await _client.PostAsJsonAsync("/api/workflows/dry-run", new
        {
            workflow = new
            {
                id = Guid.NewGuid(),
                name = "Sample author",
                triggerContentType = "timeEntry",
                triggerEvents = new[] { "Updated", "transition:ClockOut" },
                conditions = new Dictionary<string, string>(),
                actions = new[]
                {
                    new
                    {
                        type = "Email",
                        parameters = new Dictionary<string, string>
                        {
                            { "To", "{{createdBy.email}}" },
                            { "Subject", "{{transition.name}} by {{transition.by.name}}" },
                            { "Body", "{{createdBy.name}} at {{transition.at | date \"h:mm tt\" \"Asia/Manila\"}}" },
                        },
                    },
                },
            },
            sampleContent = new
            {
                id = Guid.NewGuid(),
                contentType = "timeEntry",
                status = 0,
                data = new Dictionary<string, object>(),
                createdBy = userId,
                createdAt = updatedAt.AddHours(-8),
                updatedAt,
            },
        }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync(Ct);
        text.Should().NotContain($"admin-{userId}", "the stored user's name and address are not read");

        using var doc = JsonDocument.Parse(text);
        var actions = doc.RootElement.GetProperty("actions").EnumerateArray().ToList();

        actions.Should().HaveCount(1);
        Previewed(actions[0], "To").Should().Be("sample.user@example.com");
        Previewed(actions[0], "Subject").Should().Be("ClockOut by sample.user");
        Previewed(actions[0], "Body").Should().Be("sample.user at 5:00 PM");
    }

    /// <summary>Looked up without regard to case, so the test does not depend on how dictionary keys are cased.</summary>
    private static string? Previewed(JsonElement action, string name)
    {
        var matches = action.GetProperty("resolvedParameters").EnumerateObject()
            .Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        matches.Should().HaveCount(1, "the preview carries the '{0}' parameter", name);
        return matches[0].Value.GetString();
    }
}
