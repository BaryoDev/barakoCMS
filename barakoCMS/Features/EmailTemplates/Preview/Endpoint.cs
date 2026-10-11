using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Security;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FastEndpoints;
using Marten;

namespace barakoCMS.Features.EmailTemplates.Preview;

/// <summary>
/// POST /api/email-templates/{id}/preview. Renders a template's subject and body against an entry,
/// as a workflow would send them, and sends nothing.
/// </summary>
/// <remarks>
/// For whoever writes workflows, so it asks for <c>manage_workflows</c>. On top of that the caller
/// must be able to read the template and the entry, checked as <c>GET /api/contents/{id}</c> checks
/// them, and the entry is rendered as that read shows it: a Sensitive field the caller may not see
/// is empty here, and a reference is followed only to entries the caller may read. A run resolves
/// against the whole entry, so the preview can show less than the email; it never shows more.
///
/// A template the caller cannot read answers 404, like one that does not exist, since a slug can be
/// guessed. An entry the caller cannot read answers 403, as the read by id does.
/// </remarks>
internal sealed class Endpoint(
    IQuerySession session,
    IPermissionResolver permissions,
    ISensitivityService sensitivity,
    ITemplateVariableExtractor extractor) : Endpoint<Request, Response>
{
    public override void Configure()
    {
        Post("/api/email-templates/{id}/preview");
        Definition.RequireCapability(SystemCapabilities.ManageWorkflows, "SuperAdmin", "Admin");
        Options(x => x.RequireRateLimiting(RateLimitSetup.EmailPreviewPolicy));
    }

    public override async Task HandleAsync(Request req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirst("UserId")?.Value, out var userId)
            || await session.LoadAsync<User>(userId, ct) is not { } user)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var template = await EmailTemplateRenderer.FindAsync(session, req.Id, ct);
        if (template is null || !await permissions.CanPerformActionAsync(user, template.ContentType, "read", template, ct))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var (templateShown, _, templateHidden) = await AsShownAsync(template, ct);
        if (templateHidden)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var entry = await session.LoadAsync<barakoCMS.Models.Content>(req.EntryId, ct);
        if (entry is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (!await permissions.CanPerformActionAsync(user, entry.ContentType, "read", entry, ct))
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        var (entryShown, withheld, _) = await AsShownAsync(entry, ct);

        // The layout is content too: its header and footer are shown only as the caller reads them.
        var (rendered, problem) = await EmailTemplateRenderer.RenderAsync(session, templateShown, ct,
            async (layout, token) =>
            {
                if (!await permissions.CanPerformActionAsync(user, layout.ContentType, "read", layout, token)) return null;

                var (shown, _, hidden) = await AsShownAsync(layout, token);
                return hidden ? null : shown;
            });
        if (rendered is null)
        {
            Response = new Response { Status = template.Status.ToString(), Sendable = false, Problem = problem };
            return;
        }

        var sendable = template.Status == ContentStatus.Published;

        await extractor.PreparePreviewAsync(entryShown, user.Id, [rendered.Subject, rendered.Html], ct);
        var (subject, html) = EmailTemplateRenderer.Resolve(rendered, entryShown, extractor);

        // A preview cannot know which workflow will send it, so a transition placeholder is not
        // warned about here. The workflow's own save says so when its trigger is not a transition.
        var warnings = EmailTemplateRenderer.Warnings(rendered, onTransition: true)
            .Take(WorkflowSchemaValidator.MaxPlaceholderWarnings)
            .Select(w => new PreviewWarning(w.Field, w.Message))
            .ToList();

        if (withheld > 0)
        {
            warnings.Add(new PreviewWarning("entryId",
                $"{withheld} field(s) of the entry are not shown to you, so they render empty here. A workflow run fills them."));
        }

        Response = new Response
        {
            Subject = subject,
            Html = html,
            Status = template.Status.ToString(),
            Sendable = sendable,
            Problem = sendable ? null : $"The template is {template.Status}. Only a published template is sent.",
            Warnings = warnings,
            Notes = extractor.Notes.ToList(),
        };
    }

    /// <summary>
    /// The entry as the caller's read shows it: only the fields the read leaves unchanged, so a
    /// masked value is absent and renders empty, never as its mask.
    /// </summary>
    private async Task<(FollowedContent Shown, int Withheld, bool Hidden)> AsShownAsync(
        barakoCMS.Models.Content entry, CancellationToken ct)
    {
        var stored = entry.Data ?? new Dictionary<string, object>();
        var shown = new Dictionary<string, object>(stored);
        var hidden = await sensitivity.ApplyAsync(entry, shown, HttpContext, ct);

        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, value) in shown)
        {
            if (!hidden && stored.TryGetValue(key, out var original) && ReferenceEquals(original, value)) data[key] = value;
        }

        return (new FollowedContent
        {
            Id = entry.Id,
            ContentType = hidden ? "HIDDEN" : entry.ContentType,
            Status = entry.Status,
            Sensitivity = entry.Sensitivity,
            CreatedAt = entry.CreatedAt,
            UpdatedAt = entry.UpdatedAt,
            CreatedBy = entry.CreatedBy,
            LastModifiedBy = entry.LastModifiedBy,
            Data = data,
        }, stored.Count - data.Count, hidden);
    }
}
