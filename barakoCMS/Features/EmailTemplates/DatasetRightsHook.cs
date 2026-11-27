using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.EmailTemplates;

/// <summary>
/// Refuses a save of an email template whose datasets read fields the person saving it may not read.
/// </summary>
/// <remarks>
/// It runs on every write that goes through the lifecycle hooks, a publish included. A template that
/// declares no dataset is not checked, so a write by the system is unaffected.
/// </remarks>
internal sealed class DatasetRightsHook(
    IDocumentSession session, IPermissionResolver permissions, ISensitivityService? sensitivity = null) : IContentLifecycleHook
{
    public string ContentType => EmailTemplateRenderer.TemplateType;

    public async Task<IReadOnlyList<string>> BeforeSaveAsync(IContentHookContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var (datasets, problem) = EmailTemplateRenderer.DeclaredDatasets(EmailTemplateRenderer.Text(context.Data, "Datasets"));
        if (problem is not null) return [problem];
        if (datasets.Count == 0) return [];

        var saver = await session.LoadAsync<User>(context.UserId, ct);
        return await TemplateDatasetRights.RefusalsAsync(session, permissions, sensitivity, saver, datasets, ct);
    }
}
