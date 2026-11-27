using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// What the person saving a template, or a workflow that sends one, must be able to read for each
/// dataset the template declares.
/// </summary>
/// <remarks>
/// The same check the Query step makes at workflow save. It judges the saver, so an author cannot
/// declare a dataset over fields they may not read. It does not bind later runs, which read the
/// rows as the event's actor, so a run holds the stricter of the two. A saved query that does not
/// exist yet is checked when it is first read.
/// </remarks>
internal static class TemplateDatasetRights
{
    public static async Task<List<string>> RefusalsAsync(
        IQuerySession session, IPermissionResolver permissions, barakoCMS.Core.Interfaces.ISensitivityService? sensitivity,
        User? saver, IReadOnlyDictionary<string, string> datasets, CancellationToken ct)
    {
        var refusals = new List<string>();
        if (datasets.Count == 0) return refusals;

        var runner = (IContextQueryRunner)new QueryRunner(session, permissions, sensitivity);
        foreach (var (name, slug) in datasets)
        {
            if (!TemplateDatasets.IsValidName(name))
            {
                refusals.Add($"'{TemplateDatasets.Shown(name)}' cannot be a dataset name. A name is letters, digits and underscores, starts with a letter, and is not one the template already uses.");
                continue;
            }

            if (!Slugs.IsValid(slug))
            {
                refusals.Add($"The dataset '{TemplateDatasets.Shown(name)}' does not name a saved query.");
                continue;
            }

            var saved = await session.Query<QueryDefinition>().FirstOrDefaultAsync(q => q.Slug == slug && q.DeletedAt == null, ct);
            if (saved is null) continue;

            var refusal = saver is null
                ? "Who is saving this could not be found."
                : await runner.ValidateAsync(saved, new QueryRunContext(null) { AsActor = true, Actor = saver.Id }, ct);

            if (refusal is not null)
                refusals.Add($"You cannot read everything the dataset '{TemplateDatasets.Shown(name)}' (the saved query '{slug}') reads. {refusal}");
        }

        return refusals;
    }
}
