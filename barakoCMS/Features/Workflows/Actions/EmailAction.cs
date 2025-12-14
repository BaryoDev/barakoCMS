using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Attributes;

namespace barakoCMS.Features.Workflows.Actions;

/// <summary>
/// Workflow action plugin for sending emails.
/// </summary>
[WorkflowActionMetadata(
    Description = "Send email notifications",
    RequiredParameters = new[] { "To", "Subject", "Body" },
    ExampleJson = @"{""Type"":""Email"",""Parameters"":{""To"":""admin@example.com"",""Subject"":""Workflow Triggered"",""Body"":""Content {{id}} was updated""}}"
)]
public class EmailAction : IWorkflowAction
{
    private readonly IEmailService _emailService;

    /// <summary>
    /// Creates a new EmailAction.
    /// </summary>
    public EmailAction(IEmailService emailService)
    {
        _emailService = emailService;
    }

    /// <inheritdoc />
    public string Type => "Email";

    /// <inheritdoc />
    public async Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
    {
        var to = parameters.GetValueOrDefault("To", "admin@example.com");
        var subject = parameters.GetValueOrDefault("Subject", $"Workflow Triggered for Content {content.Id}");
        var body = parameters.GetValueOrDefault("Body", $"Content '{content.ContentType}' with ID {content.Id} triggered this workflow.");

        await _emailService.SendEmailAsync(to, subject, body, ct);
    }
}

