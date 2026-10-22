using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Attributes;
using barakoCMS.Models;
using Marten;
using JasperFx;

namespace BarakoCMS.Documents;

/// <summary>
/// Renders an entry to a PDF and stores it through the Files module.
/// <c>{ "Type": "RenderDocument", "Parameters": { "Template": "invoice" } }</c>. The stored file's id
/// is the <c>FileId</c> output.
/// </summary>
/// <remarks>
/// The file is private and owned by nobody. Two runs under the same idempotency key give one file:
/// the second returns the first's id. A run with no key has nothing to be matched on, so a retry
/// stores another file.
/// </remarks>
[WorkflowActionMetadata(
    Description = "Render the entry to a PDF from a document template and store it as a file",
    Group = WorkflowActionGroup.Delivery,
    RequiredParameters = new[] { "Template" },
    ExampleJson = @"{""Type"":""RenderDocument"",""Parameters"":{""Template"":""invoice""}}"
)]
internal sealed class RenderDocumentAction(IDocumentSession session, IFileStore files) : IWorkflowAction
{
    private static readonly TimeSpan ClaimLifetime = TimeSpan.FromMinutes(5);
    private const string InvalidNameCharacters = "<>:\"|?*";

    public string Type => "RenderDocument";

    [Obsolete("RunAsync is the contract this action implements.")]
    public Task ExecuteAsync(Dictionary<string, string> parameters, Content content, CancellationToken ct) =>
        ((IWorkflowAction)this).RunAsync(parameters, content, ct);

    public async Task<WorkflowActionResult> RunAsync(
        Dictionary<string, string> parameters, Content content, CancellationToken ct)
    {
        var slug = parameters.GetValueOrDefault("Template");
        if (string.IsNullOrWhiteSpace(slug))
        {
            return WorkflowActionResult.PermanentFailure("No 'Template' parameter names a document template.");
        }

        if (parameters.GetValueOrDefault("TriggerEvent") == "Deleted")
        {
            return WorkflowActionResult.PermanentFailure("A deleted entry has no data to render.");
        }

        var template = await session.Query<DocumentTemplate>().OrderBy(t => t.Id).FirstOrDefaultAsync(t => t.Slug == slug, ct);
        if (template is null)
        {
            return WorkflowActionResult.PermanentFailure($"No document template with the slug '{slug}'.");
        }

        if (!string.Equals(template.ContentType, content.ContentType, StringComparison.OrdinalIgnoreCase))
        {
            return WorkflowActionResult.PermanentFailure(
                $"Template '{slug}' renders '{template.ContentType}', not '{content.ContentType}'.");
        }

        parameters.TryGetValue("IdempotencyKey", out var key);
        var renderId = string.IsNullOrEmpty(key) ? null : DocumentRender.KeyFor(template.Id, key);

        if (renderId is not null && await session.LoadAsync<DocumentRender>(renderId, ct) is { FileId: var existing } && existing != Guid.Empty)
        {
            return Produced(existing);
        }

        // Checked on every render, not only when the template was saved: a field marked Sensitive
        // after that has to stop the next document.
        var schema = await session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(c => c.Name == content.ContentType, ct);

        if (DocumentTemplateGuard.Refuse(template, schema) is { } refusal)
        {
            return WorkflowActionResult.PermanentFailure($"Template '{slug}' was refused: {refusal}");
        }

        var parsed = DocumentTemplateParser.Parse(template.Source);
        if (parsed.Blocks.Count == 0)
        {
            return WorkflowActionResult.PermanentFailure($"Template '{slug}' has no content to render.");
        }

        byte[] pdf;
        try
        {
            pdf = PdfRenderer.Render(parsed, content);
        }
        catch (DocumentTooLongException ex)
        {
            return WorkflowActionResult.PermanentFailure(ex.Message);
        }

        if (renderId is not null && !await ClaimAsync(renderId, ct))
        {
            return await session.LoadAsync<DocumentRender>(renderId, ct) is { FileId: var won } && won != Guid.Empty
                ? Produced(won)
                : WorkflowActionResult.Failure("Another run is rendering this document. Try again.");
        }

        return await StoreAsync(pdf, FileNameFor(template, content), renderId, ct);
    }

    /// <summary>
    /// Writes the run's record before the file, so two runs under one key cannot both store. The
    /// loser sees the insert refused. A claim that never got a file (the process died) is taken over
    /// once it is old enough that its run cannot still be going.
    /// </summary>
    private async Task<bool> ClaimAsync(string renderId, CancellationToken ct)
    {
        var claim = new DocumentRender { Id = renderId, FileId = Guid.Empty, ClaimedAt = DateTimeOffset.UtcNow };
        var stale = await session.LoadAsync<DocumentRender>(renderId, ct) is { FileId: var f, ClaimedAt: var at }
            && f == Guid.Empty && DateTimeOffset.UtcNow - at > ClaimLifetime;

        try
        {
            if (stale) session.Store(claim); else session.Insert(claim);
            await session.SaveChangesAsync(ct);
            return true;
        }
        catch (DocumentAlreadyExistsException)
        {
            return false;
        }
    }

    private async Task<WorkflowActionResult> StoreAsync(byte[] pdf, string name, string? renderId, CancellationToken ct)
    {
        // Nothing is staged on the session here: the store refuses to save over staged work.
        FileSaveResult saved;
        try
        {
            using var stream = new MemoryStream(pdf, writable: false);
            saved = await files.SaveAsync(
                new FileToStore { Content = stream, FileName = name, ContentType = "application/pdf" }, ct);
        }
        catch
        {
            await ReleaseAsync(renderId);
            throw;
        }

        if (saved.File is null)
        {
            await ReleaseAsync(renderId);
            return saved.CanRetry
                ? WorkflowActionResult.Failure(saved.Refused ?? "The file store did not take the document.")
                : WorkflowActionResult.PermanentFailure(saved.Refused ?? "The file store refused the document.");
        }

        if (renderId is not null)
        {
            session.Store(new DocumentRender { Id = renderId, FileId = saved.File.Id, ClaimedAt = DateTimeOffset.UtcNow });
            await session.SaveChangesAsync(ct);
        }

        return Produced(saved.File.Id);
    }

    private async Task ReleaseAsync(string? renderId)
    {
        if (renderId is null) return;
        session.Delete<DocumentRender>(renderId);
        await session.SaveChangesAsync(CancellationToken.None);
    }

    private static string FileNameFor(DocumentTemplate template, Content content)
    {
        var name = string.IsNullOrWhiteSpace(template.FileName) ? template.Slug : template.FileName;
        name = DocumentValues.Fill(name, content);
        name = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
        name = new string(name.Where(c => !char.IsControl(c) && !InvalidNameCharacters.Contains(c)).ToArray()).Trim();
        if (name.Length == 0) name = template.Slug;
        if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name += ".pdf";
        return name;
    }

    private static WorkflowActionResult Produced(Guid fileId) =>
        WorkflowActionResult.Success(new Dictionary<string, string> { ["FileId"] = fileId.ToString() });
}
