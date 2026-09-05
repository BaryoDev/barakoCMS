using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Workflows.Actions;

/// <summary>
/// Sends a configured request through a configured connector, so an integration is described rather
/// than written.
/// </summary>
/// <remarks>
/// The whole configuration surface in a workflow definition is one parameter:
/// <c>{ "Type": "Request", "Parameters": { "Request": "post-to-facebook" } }</c>. Everything else
/// lives on the request and the connector, where each can be tested on its own.
///
/// This implements <c>RunAsync</c> rather than <c>ExecuteAsync</c>, which is the point of #224: a
/// provider answering 401, a template naming a Sensitive field, a connector that was deleted, are
/// all expected outcomes of a configured action rather than defects, and the run record has to be
/// able to say which happened.
/// </remarks>
[barakoCMS.Infrastructure.Attributes.WorkflowActionMetadata(
    Description = "Send a configured request through a configured connector",
    RequiredParameters = new[] { "Request" },
    ExampleJson = @"{""Type"":""Request"",""Parameters"":{""Request"":""post-to-facebook""}}"
)]
internal sealed class RequestAction : IWorkflowAction
{
    public string Type => "Request";

    private readonly IQuerySession _session;
    private readonly IRequestComposer _composer;
    private readonly IConnectorSender _sender;
    private readonly ILogger<RequestAction> _logger;

    public RequestAction(
        IQuerySession session,
        IRequestComposer composer,
        IConnectorSender sender,
        ILogger<RequestAction> logger)
    {
        _session = session;
        _composer = composer;
        _sender = sender;
        _logger = logger;
    }

    /// <summary>
    /// Only here because the interface still declares it. <see cref="RunAsync"/> is the contract
    /// this action implements, and delegating keeps a caller on the older path behaving the same.
    /// </summary>
    public Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct) =>
        RunAsync(parameters, content, ct);

    public async Task<WorkflowActionResult> RunAsync(
        Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
    {
        var slug = parameters.GetValueOrDefault("Request");
        if (string.IsNullOrWhiteSpace(slug))
        {
            return WorkflowActionResult.Failure("No 'Request' parameter names a request definition.");
        }

        var definition = await _session.Query<RequestDefinition>()
            .FirstOrDefaultAsync(r => r.Slug == slug, ct);

        if (definition is null)
        {
            return WorkflowActionResult.Failure($"No request definition with the slug '{slug}'.");
        }

        var connector = await _session.Query<Connector>()
            .FirstOrDefaultAsync(c => c.Slug == definition.ConnectorSlug, ct);

        if (connector is null)
        {
            // Named, because a connector deleted out from under a workflow turns every run into a
            // failure whose message otherwise says nothing about which piece went missing.
            return WorkflowActionResult.Failure(
                $"Request '{slug}' names connector '{definition.ConnectorSlug}', which does not exist.");
        }

        if (!connector.Enabled)
        {
            return WorkflowActionResult.Failure($"Connector '{connector.Slug}' is disabled.");
        }

        // Present when the runner queued this attempt (WorkflowRunner.cs sets it on every action's
        // parameters), absent when this action was invoked some other way. Passed through unchanged:
        // WebhookAction sends the same runner-supplied value verbatim, and the two paths have to
        // agree for a receiver comparing them to see one call rather than two.
        parameters.TryGetValue("IdempotencyKey", out var idempotencyKey);

        var composed = await _composer.ComposeAsync(definition, connector, content, idempotencyKey, ct);
        if (!composed.Ok)
        {
            _logger.LogWarning("Request '{Slug}' was refused before sending: {Reason}", slug, composed.Refusal);
            return WorkflowActionResult.Failure(composed.Refusal!);
        }

        var result = await _sender.SendAsync(connector, composed, definition.Success, definition.SuccessJsonPath, ct);

        return result.Succeeded
            ? WorkflowActionResult.Success()
            : WorkflowActionResult.Failure($"Request '{slug}': {result.Error ?? result.Describe()}");
    }
}
