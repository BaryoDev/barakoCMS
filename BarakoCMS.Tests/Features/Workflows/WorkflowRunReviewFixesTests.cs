using FluentAssertions;
using barakoCMS.Features.Workflows;
using barakoCMS.Features.Workflows.Actions;
using barakoCMS.Models;
using Xunit;

namespace BarakoCMS.Tests.Features.Workflows;

/// <summary>
/// The three things the review on #458 found, each pinned so it cannot come back.
/// </summary>
public class WorkflowRunReviewFixesTests
{
    private static Content Entry(ContentStatus status, Dictionary<string, object>? data = null) => new()
    {
        Id = Guid.NewGuid(),
        ContentType = "article",
        Status = status,
        Data = data ?? new Dictionary<string, object>(),
    };

    private static WorkflowDefinition Workflow(string key, string value) => new()
    {
        Id = Guid.NewGuid(),
        Name = "on publish",
        TriggerContentType = "article",
        TriggerEvent = "Published",
        Conditions = new Dictionary<string, string> { [key] = value },
    };

    /// <summary>
    /// A "Status" field in the data must not shadow the lifecycle status.
    /// </summary>
    /// <remarks>
    /// The data bag was consulted first, so an entry modelling its own Status field, which is an
    /// ordinary thing to model, answered the condition instead of the document. A workflow
    /// conditioned on Status Published then fired on whatever that field said, and nothing named the
    /// system property, so the two were indistinguishable from inside the rule.
    /// </remarks>
    [Fact]
    public void A_status_field_in_the_data_does_not_shadow_the_lifecycle_status()
    {
        var shadowed = Entry(ContentStatus.Draft, new Dictionary<string, object> { ["Status"] = "Published" });

        WorkflowConditions.Matches(Workflow("Status", "Published"), shadowed).Should().BeFalse(
            "the entry is a Draft, whatever a field of its own happens to be called");

        WorkflowConditions.Matches(Workflow("Status", "Draft"), shadowed).Should().BeTrue(
            "and the document's own status is what answers");
    }

    [Fact]
    public void An_ordinary_data_field_still_answers_its_own_condition()
    {
        // The pairing. Checking Status first would be worth nothing if it had broken every other
        // condition on the way past.
        var entry = Entry(ContentStatus.Published, new Dictionary<string, object> { ["Region"] = "north" });

        WorkflowConditions.Matches(Workflow("Region", "north"), entry).Should().BeTrue();
        WorkflowConditions.Matches(Workflow("Region", "south"), entry).Should().BeFalse();
        WorkflowConditions.Matches(Workflow("Missing", "anything"), entry).Should().BeFalse(
            "a condition naming a field the entry does not have cannot match");
    }

    /// <summary>
    /// A webhook URL is written down without whatever it uses to authenticate.
    /// </summary>
    /// <remarks>
    /// The error text is persisted on the run and returned by the workflow-run API, so an unredacted
    /// URL puts a live credential in the database, in an API response and in the logs, readable by
    /// everyone who can view runs rather than only by whoever configured it. Userinfo and the query
    /// string are the two places a webhook URL routinely carries one.
    /// </remarks>
    [Theory]
    [InlineData("https://user:s3cret@hooks.example.com/endpoint", "https://hooks.example.com/endpoint")]
    [InlineData("https://hooks.example.com/endpoint?key=s3cret", "https://hooks.example.com/endpoint")]
    [InlineData("https://user:s3cret@hooks.example.com/endpoint?key=other", "https://hooks.example.com/endpoint")]
    [InlineData("https://hooks.example.com:8443/a/b", "https://hooks.example.com:8443/a/b")]
    public void A_webhook_url_keeps_its_host_and_path_and_loses_its_secrets(string url, string expected)
    {
        var redacted = WebhookAction.Redact(url);

        redacted.Should().Be(expected);
        redacted.Should().NotContain("s3cret", "the whole point is that the secret does not survive");
    }

    /// <summary>
    /// No log call in this action is handed a raw URL or an exception object.
    /// </summary>
    /// <remarks>
    /// Read off the source rather than driven, because the leak is a call shape rather than a
    /// behaviour: `LogError(ex, ...)` writes the exception's Message, and a transport failure raised
    /// against a URL carrying a token in its query can carry that URL into the log aggregator. The
    /// redaction on the returned error text does nothing about it.
    ///
    /// A structural check catches the next one too. The first pass at this redacted four call sites
    /// and missed the fifth, which logged the raw URL on the guard-refusal path, and no behavioural
    /// test would have noticed because that path returns a redacted message either way.
    /// </remarks>
    [Fact]
    public void No_log_call_in_the_webhook_action_is_handed_a_raw_url_or_an_exception()
    {
        var source = File.ReadAllText(SourcePath("barakoCMS/Features/Workflows/Actions/WebhookAction.cs"));

        var logCalls = source.Split("_logger.Log").Skip(1).ToList();

        logCalls.Should().NotBeEmpty("this asserts over the call sites, so finding none would pass silently");

        foreach (var call in logCalls)
        {
            var head = call[..Math.Min(call.Length, 400)];

            head.Should().NotContain(", url)", "a raw URL must be redacted before it is logged");
            head.Should().NotContain(", url,", "a raw URL must be redacted before it is logged");

            // The split consumed "_logger.Log", so each chunk begins at the level: "Error(ex, ...".
            // Matching on "Log*(ex," here found nothing and passed, which the mutation caught.
            head.Should().NotContain("(ex,", "the exception object carries a Message that can hold the URL");
        }
    }

    /// <summary>Walks up from the test binary to the repository root.</summary>
    private static string SourcePath(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the repository root has to be findable or this test is checking nothing");

        return Path.Combine(dir!.FullName, relative);
    }

    [Fact]
    public void A_url_that_cannot_be_parsed_says_so_rather_than_being_echoed()
    {
        // Falling back to the raw string would put the unparseable value, secrets and all, into the
        // place this method exists to keep them out of.
        var redacted = WebhookAction.Redact("not a url at all ?key=s3cret");

        redacted.Should().NotContain("s3cret");
        redacted.Should().Contain("could not be parsed");
    }
}
