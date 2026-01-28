using barakoCMS.Features.WorkflowsV2.Models;
using barakoCMS.Features.WorkflowsV2.Services;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.WorkflowsV2.Actions.Communication;

/// <summary>
/// Send email notification using templates.
/// </summary>
public class SendEmailAction : BaseWorkflowAction
{
    private readonly IEmailTemplateService _templateService;
    private readonly IEmailSenderService _emailSender;

    public SendEmailAction(
        IEmailTemplateService templateService,
        IEmailSenderService emailSender,
        ILogger<SendEmailAction> logger) : base(logger)
    {
        _templateService = templateService;
        _emailSender = emailSender;
    }

    public override string Type => "SendEmail";
    public override string Name => "Send Email";
    public override string Description => "Send an email notification using a template or custom content.";
    public override string Category => ActionCategories.Communication;

    public override async Task<ActionResult> ExecuteAsync(WorkflowActionV2 action, WorkflowContext context)
    {
        try
        {
            var to = GetStringList(action.Config, "to", context);
            var cc = GetStringList(action.Config, "cc", context);
            var bcc = GetStringList(action.Config, "bcc", context);

            if (to.Count == 0)
            {
                return Failure("No recipients specified in 'to' field.");
            }

            string subject;
            string htmlBody;
            string? textBody = null;

            var templateName = GetString(action.Config, "template", context);

            if (!string.IsNullOrEmpty(templateName))
            {
                // Load template
                var template = await _templateService.GetTemplateAsync(templateName);
                if (template == null)
                {
                    return Failure($"Email template '{templateName}' not found.");
                }

                var variables = BuildTemplateVariables(action.Config, context);
                subject = _templateService.RenderTemplate(template.Subject, variables);
                htmlBody = _templateService.RenderTemplate(template.HtmlBody, variables);
                textBody = string.IsNullOrEmpty(template.TextBody)
                    ? null
                    : _templateService.RenderTemplate(template.TextBody, variables);
            }
            else
            {
                // Use inline content
                subject = GetRequiredString(action.Config, "subject", context);
                htmlBody = GetString(action.Config, "body", context);

                if (string.IsNullOrEmpty(htmlBody))
                {
                    htmlBody = GetString(action.Config, "htmlBody", context);
                }

                textBody = GetString(action.Config, "textBody", context);
            }

            var fromEmail = GetString(action.Config, "from", context);
            var fromName = GetString(action.Config, "fromName", context);
            var replyTo = GetString(action.Config, "replyTo", context);

            if (context.IsDryRun)
            {
                Logger.LogInformation("[DRY-RUN] Would send email to {To} with subject: {Subject}",
                    string.Join(", ", to), subject);

                return Success(new Dictionary<string, object>
                {
                    ["dryRun"] = true,
                    ["to"] = to,
                    ["subject"] = subject
                });
            }

            await _emailSender.SendAsync(new Services.EmailMessage
            {
                To = to,
                Cc = cc,
                Bcc = bcc,
                Subject = subject,
                HtmlBody = htmlBody,
                TextBody = textBody,
                From = !string.IsNullOrEmpty(fromName) && !string.IsNullOrEmpty(fromEmail)
                    ? $"{fromName} <{fromEmail}>"
                    : fromEmail,
                ReplyTo = replyTo
            });

            Logger.LogInformation("Sent email to {To} with subject: {Subject}",
                string.Join(", ", to), subject);

            return Success(new Dictionary<string, object>
            {
                ["sent"] = true,
                ["to"] = to,
                ["subject"] = subject
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to send email");
            return Failure($"Failed to send email: {ex.Message}");
        }
    }

    private Dictionary<string, object> BuildTemplateVariables(Dictionary<string, object> config, WorkflowContext context)
    {
        var variables = new Dictionary<string, object>
        {
            ["content"] = new Dictionary<string, object>
            {
                ["id"] = context.Content.Id,
                ["contentType"] = context.Content.ContentType,
                ["status"] = context.Content.Status.ToString(),
                ["data"] = context.Content.Data
            },
            ["system"] = new Dictionary<string, object>
            {
                ["now"] = DateTime.UtcNow,
                ["baseUrl"] = context.HttpContext != null
                    ? $"{context.HttpContext.Request.Scheme}://{context.HttpContext.Request.Host}"
                    : "http://localhost"
            }
        };

        if (context.User != null)
        {
            variables["user"] = new Dictionary<string, object>
            {
                ["id"] = context.User.Id,
                ["username"] = context.User.Username,
                ["email"] = context.User.Email
            };
        }

        // Add custom variables from config
        var customVars = GetDictionary(config, "variables");
        foreach (var kv in customVars)
        {
            var resolvedValue = ResolveTemplateVariables(kv.Value?.ToString() ?? "", context);
            variables[kv.Key] = resolvedValue;
        }

        return variables;
    }

    public override List<string> ValidateConfig(Dictionary<string, object> config)
    {
        var errors = new List<string>();

        if (!config.ContainsKey("to") && !config.ContainsKey("template"))
        {
            errors.Add("Either 'to' or 'template' must be specified.");
        }

        if (!config.ContainsKey("template"))
        {
            if (!config.ContainsKey("subject"))
                errors.Add("'subject' is required when not using a template.");

            if (!config.ContainsKey("body") && !config.ContainsKey("htmlBody"))
                errors.Add("'body' or 'htmlBody' is required when not using a template.");
        }

        return errors;
    }

    public override ActionConfigSchema GetConfigSchema()
    {
        return new ActionConfigSchema
        {
            Type = Type,
            Properties = new List<ActionConfigProperty>
            {
                new() { Name = "to", Type = "array", Description = "Recipient email addresses", Required = true },
                new() { Name = "cc", Type = "array", Description = "CC email addresses" },
                new() { Name = "bcc", Type = "array", Description = "BCC email addresses" },
                new() { Name = "template", Type = "string", Description = "Email template name to use" },
                new() { Name = "subject", Type = "string", Description = "Email subject (if not using template)" },
                new() { Name = "body", Type = "string", Description = "Email body HTML (if not using template)" },
                new() { Name = "textBody", Type = "string", Description = "Plain text alternative" },
                new() { Name = "from", Type = "string", Description = "From email address" },
                new() { Name = "fromName", Type = "string", Description = "From display name" },
                new() { Name = "replyTo", Type = "string", Description = "Reply-to email address" },
                new() { Name = "variables", Type = "object", Description = "Additional template variables" }
            },
            Required = new List<string> { "to" },
            Example = @"{""to"": [""{{data.email}}""], ""template"": ""welcome-email""}"
        };
    }
}
