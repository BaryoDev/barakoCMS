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
