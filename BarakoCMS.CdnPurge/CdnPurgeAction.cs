using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Attributes;
using barakoCMS.Infrastructure.Caching;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;

namespace BarakoCMS.CdnPurge;

/// <summary>
/// Purges the cache tags a published entry appears under: its entry tag, its type tag and the
/// tenant's sitemap tag.
/// </summary>
/// <remarks>
/// The tags come from <see cref="DeliveryCache.PurgeTags"/>, the builder delivery reads use, so a
/// purge names exactly what a read emitted. Every tag starts with the run's tenant. Add it to a
/// workflow that triggers on publish. A purge the CDN rejects is a failed action on the run, not a
/// log line.
/// </remarks>
[WorkflowActionMetadata(
    Description = "Purge the CDN cache tags of this entry, its type and the sitemap",
    Group = WorkflowActionGroup.Delivery,
    RequiredParameters = new string[0],
    OptionalParameters = new string[0],
    ExampleJson = @"{""Type"":""CdnPurge"",""Parameters"":{}}"
)]
internal sealed class CdnPurgeAction(ICdnPurger purger, TenantContext tenant) : IWorkflowAction
{
    public const string ActionType = "CdnPurge";

    public string Type => ActionType;

    public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        RunAsync(parameters, content, ct);

    public async Task<WorkflowActionResult> RunAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct)
    {
        var tags = TagsFor(tenant.Slug, content);
        var result = await purger.PurgeTagsAsync(tags, ct);
        if (result.Succeeded)
            return WorkflowActionResult.Success();

        var error = result.Error ?? "The CDN purge failed.";
        var outcome = result.Retryable
            ? WorkflowActionResult.Failure(error)
            : WorkflowActionResult.PermanentFailure(error);
        return result.Status is { } status ? outcome with { ResponseStatus = status } : outcome;
    }

    internal static IReadOnlyList<string> TagsFor(string tenantSlug, Content content) =>
        DeliveryCache.PurgeTags(tenantSlug,
        [
            CacheScope.Entry(content.Id),
            CacheScope.Type(content.ContentType),
            CacheScope.Named("sitemap"),
        ]);
}
