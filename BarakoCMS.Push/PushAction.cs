using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Attributes;
using barakoCMS.Models;

namespace BarakoCMS.Push;

[WorkflowActionMetadata(
    Description = "Send a web push notification to everyone in the tenant who has subscribed.",
    Group = WorkflowActionGroup.Comms,
    RequiredParameters = ["Title"],
    OptionalParameters = ["Body", "Url"],
    ExampleJson = "{\"Title\":\"{{data.Title}}\",\"Body\":\"{{data.Body}}\",\"Url\":\"/news\"}")]
public sealed class PushAction(IPushSender sender) : IWorkflowAction
{
    public string Type => "Push";

    public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        throw new NotSupportedException("Push needs the tenant, so it only runs through RunAsync(WorkflowContext).");

    public async Task<WorkflowActionResult> RunAsync(WorkflowContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.TenantId))
            return WorkflowActionResult.PermanentFailure("Push needs a tenant.");
        if (!context.Parameters.TryGetValue("Title", out var title) || string.IsNullOrWhiteSpace(title))
            return WorkflowActionResult.PermanentFailure("Push needs a Title.");
        // Body is optional in effect: a missing key, an empty string or a template hole that did not
        // resolve (a title-only record) sends the title alone.
        context.Parameters.TryGetValue("Body", out var body);
        if (string.IsNullOrWhiteSpace(body) || IsUnresolvedHole(body)) body = "";
        context.Parameters.TryGetValue("Url", out var url);

        var n = new PushNotification(Trim(title, 80), Trim(body, 140), PushNotification.SafeUrl(url));
        try { n.ToJson(); }
        catch (ArgumentException) { return WorkflowActionResult.PermanentFailure("Push payload is too large."); }

        await sender.SendToTenantAsync(context.TenantId, n, ct);
        return WorkflowActionResult.Success();
    }

    private static bool IsUnresolvedHole(string s)
    {
        var t = s.Trim();
        return t.StartsWith("{{", StringComparison.Ordinal) && t.EndsWith("}}", StringComparison.Ordinal)
            && t.IndexOf("{{", 2, StringComparison.Ordinal) < 0;
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";
}
