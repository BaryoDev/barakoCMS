namespace barakoCMS.Features.Workflows.Actions;

/// <summary>
/// What a caller of <see cref="WebhookAction"/> supplies in place of the content payload: the exact
/// body to send and the headers to add to the request.
/// </summary>
internal sealed record OutboundOverride(byte[] Body, IReadOnlyDictionary<string, string> Headers);
