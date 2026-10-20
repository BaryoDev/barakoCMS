using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Workflows;
using barakoCMS.Infrastructure.Attributes;
using barakoCMS.Models;
using Marten;

namespace BarakoCMS.Documents;

/// <summary>
/// Renders an entry to a PDF and stores it through the Files module.
/// <c>{ "Type": "RenderDocument", "Parameters": { "Template": "invoice" } }</c>. The stored file's id
/// is the <c>FileId</c> output.
/// </summary>
/// <remarks>
/// The file is private and owned by nobody. Two runs under the same idempotency key give one file:
/// the second returns the first's id.
/// </remarks>
[WorkflowActionMetadata(
    Description = "Render the entry to a PDF from a document template and store it as a file",
    Group = WorkflowActionGroup.Delivery,
    RequiredParameters = new[] { "Template" },
    ExampleJson = @"{""Type"":""RenderDocument"",""Parameters"":{""Template"":""invoice""}}"
)]
internal sealed class RenderDocumentAction(IDocumentSession session, IFileStore files) : IWorkflowAction
{
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

        var template = await session.Query<DocumentTemplate>().FirstOrDefaultAsync(t => t.Slug == slug, ct);
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

        if (renderId is not null && await session.LoadAsync<DocumentRender>(renderId, ct) is { } done)
        {
            return Produced(done.FileId);
        }

        // Checked on every render, not only when the template was saved: a field marked Sensitive
        // after that has to stop the next document.
        var schema = await session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(c => c.Name == content.ContentType, ct);

        if (DocumentTemplateGuard.Refuse(template, schema) is { } refusal)
        {
            return WorkflowActionResult.PermanentFailure($"Template '{slug}' was refused: {refusal}");
        }

        byte[] pdf;
        try
        {
            pdf = PdfRenderer.Render(DocumentTemplateParser.Parse(template.Source), content);
        }
        catch (DocumentTooLongException ex)
        {
            return WorkflowActionResult.PermanentFailure(ex.Message);
        }

        var name = string.IsNullOrWhiteSpace(template.FileName) ? template.Slug : template.FileName;
        name = Path.GetFileName(DocumentValues.Fill(name, content)).Trim();
        if (name.Length == 0) name = template.Slug;
        if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name += ".pdf";

        // Nothing is staged on the session yet: the store refuses to save over staged work.
        FileSaveResult saved;
        using (var stream = new MemoryStream(pdf, writable: false))
        {
            saved = await files.SaveAsync(
                new FileToStore { Content = stream, FileName = name, ContentType = "application/pdf" }, ct);
        }

        if (saved.File is null)
        {
            return saved.CanRetry
                ? WorkflowActionResult.Failure(saved.Refused ?? "The file store did not take the document.")
                : WorkflowActionResult.PermanentFailure(saved.Refused ?? "The file store refused the document.");
        }

        if (renderId is not null)
        {
            session.Store(new DocumentRender { Id = renderId, FileId = saved.File.Id });
            await session.SaveChangesAsync(ct);
        }

        return Produced(saved.File.Id);
    }

    private static WorkflowActionResult Produced(Guid fileId) =>
        WorkflowActionResult.Success(new Dictionary<string, string> { ["FileId"] = fileId.ToString() });
}
