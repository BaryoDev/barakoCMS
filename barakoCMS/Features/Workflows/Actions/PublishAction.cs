using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using barakoCMS.Features.Workflows.Outbox;
using barakoCMS.Infrastructure.Attributes;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Workflows.Actions;

/// <summary>
/// Publishes a message about an entry to a service outside the CMS, through the outbox.
/// </summary>
/// <remarks>
/// The action does not make the call. It writes an <see cref="OutboxMessage"/> that is saved with the
/// run's record of the action, and the action succeeds once the message is stored. A sender delivers
/// it afterwards, signed, and retries when the receiver is down. See <see cref="OutboxSender"/>.
///
/// What is sent is what <c>Payload</c> names, nothing else: a JSON object of text values whose
/// placeholders are limited to <see cref="PublishPayload"/>'s allowlist. A field that is not Public is
/// refused when the workflow is saved, and checked again here, since the type can change after the save.
/// </remarks>
[WorkflowActionMetadata(
    Description = "Publish a message to an outside service through a signed, retried outbox. Only the fields the Payload names are sent",
    Group = WorkflowActionGroup.Delivery,
    RequiredParameters = new[] { "Url", "Topic", "MessageType", "Payload" },
    OptionalParameters = new[] { "Secret" },
    ExampleJson = @"{""Type"":""Publish"",""Parameters"":{""Url"":""https://example.com/events"",""Topic"":""articles"",""MessageType"":""article.published"",""Payload"":""{\""id\"":\""{{id}}\"",\""title\"":\""{{data.Title}}\""}"",""Secret"":""a shared secret, optional""}}"
)]
internal class PublishAction : IWorkflowAction
{
    private static readonly JsonSerializerOptions EnvelopeJson = new(JsonSerializerDefaults.Web);

    private readonly IDocumentSession _session;
    private readonly OutboxStaging _staging;
    private readonly ILogger<PublishAction> _logger;

    public PublishAction(IDocumentSession session, OutboxStaging staging, ILogger<PublishAction> logger)
    {
        _session = session;
        _staging = staging;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Type => "Publish";

    /// <inheritdoc />
    public async Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
        => await RunAsync(parameters, content, ct);

    /// <inheritdoc />
    public async Task<WorkflowActionResult> RunAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
    {
        var url = parameters.GetValueOrDefault("Url");
        var topic = parameters.GetValueOrDefault("Topic");
        var messageType = parameters.GetValueOrDefault("MessageType");

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(topic) || string.IsNullOrWhiteSpace(messageType))
        {
            return WorkflowActionResult.PermanentFailure("Publish needs a Url, a Topic and a MessageType.");
        }

        if (content.Sensitivity != SensitivityLevel.Public)
        {
            return WorkflowActionResult.PermanentFailure("The entry is not Public, so nothing from it is published.");
        }

        var (template, parseError) = PublishPayload.Parse(parameters.GetValueOrDefault(PublishPayload.PayloadParameter));
        if (template is null)
        {
            return WorkflowActionResult.PermanentFailure(parseError!);
        }

        var definition = await _session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name == content.ContentType, ct);

        var problems = PublishPayload.Problems(
            parameters[PublishPayload.PayloadParameter], definition is null ? [] : [definition]);
        if (problems.Count > 0)
        {
            return WorkflowActionResult.PermanentFailure("The payload cannot be published. " + string.Join(". ", problems));
        }

        // No schema says which fields are Public, so there is no basis for sending any of them.
        if (definition is null && PublishPayload.Placeholders(template.Values).Fields.Count > 0)
        {
            return WorkflowActionResult.PermanentFailure($"Content type '{content.ContentType}' has no definition, so its fields cannot be published.");
        }

        var payload = new Dictionary<string, string>(template.Count, StringComparer.Ordinal);
        foreach (var (name, value) in template)
        {
            payload[name] = TemplateVariableExtractor.Resolve(value, content, TemplateValueEncoding.None);
        }

        var tenant = TenantScopes.SlugFor(_session.TenantId);
        var now = DateTimeOffset.UtcNow;
        var id = MessageId(parameters.GetValueOrDefault("IdempotencyKey"));

        var message = new OutboxMessage
        {
            Id = id,
            Tenant = tenant,
            RunId = Guid.TryParse(parameters.GetValueOrDefault("RunId"), out var runId) ? runId : null,
            WorkflowId = Guid.TryParse(parameters.GetValueOrDefault("WorkflowId"), out var workflowId) ? workflowId : null,
            ContentType = content.ContentType,
            ContentId = content.Id,
            Topic = topic.Trim(),
            MessageType = messageType.Trim(),
            Url = url.Trim(),
            Secret = WebhookSigning.HasSecret(parameters) ? parameters[WebhookSigning.SecretParameter] : null,
            CreatedAt = now,
            NextAttemptAt = now,
            Body = JsonSerializer.Serialize(new
            {
                messageId = id,
                topic = topic.Trim(),
                messageType = messageType.Trim(),
                tenant,
                createdAt = now,
                payload,
            }, EnvelopeJson),
        };

        if (_staging.Holding)
        {
            _staging.Messages.Add(message);
        }
        else
        {
            _session.Store(message);
            await _session.SaveChangesAsync(ct);
        }

        _logger.LogInformation("Queued outbox message {MessageId} for topic {Topic}", id, message.Topic);
        return WorkflowActionResult.Success();
    }

    /// <summary>
    /// The runner's idempotency key as a message id, so a re-run of the same attempt queues the same
    /// message. A caller without one gets a new id each time.
    /// </summary>
    private static Guid MessageId(string? idempotencyKey) =>
        string.IsNullOrWhiteSpace(idempotencyKey)
            ? Guid.NewGuid()
            : new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("publish:" + idempotencyKey))[..16]);
}
