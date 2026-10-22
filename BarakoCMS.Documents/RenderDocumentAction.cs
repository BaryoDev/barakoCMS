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
    internal static readonly TimeSpan ClaimLifetime = TimeSpan.FromMinutes(5);
    private const int MaxClaimAttempts = 20;
    private const int MaxNameCharacters = 200;
    private const string InvalidNameCharacters = "<>:\"|?*";

    public string Type => "RenderDocument";

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

        var claim = Claim.None;
        if (renderId is not null)
        {
            claim = await ClaimAsync(renderId, ct);
            if (claim.Done is { } won) return Produced(won);
            if (claim.Id is null) return WorkflowActionResult.Failure("Another run is rendering this document. Try again.");
        }

        return await StoreAsync(pdf, FileNameFor(template, content), renderId, claim.Id, ct);
    }

    private readonly record struct Claim(string? Id, Guid? Done)
    {
        public static Claim None => new(null, null);
    }

    /// <summary>
    /// Takes the run's claim before the file is stored, so two runs under one key cannot both store.
    /// A claim row is only ever inserted, never overwritten, so the database lets exactly one run
    /// own each generation. A generation with no result after <see cref="ClaimLifetime"/> belonged
    /// to a run that died or is far too slow, and the next run takes the next generation: two
    /// retries racing for it get one winner, and the original run cannot clobber or release a claim
    /// that is not its own, because it only ever writes its own generation's row.
    /// Returns the claim id, or the file id when the work turned out to be done, or neither when a
    /// live run holds it.
    /// </summary>
    private async Task<Claim> ClaimAsync(string resultKey, CancellationToken ct)
    {
        var generation = 0;
        for (var attempt = 0; attempt < MaxClaimAttempts; attempt++)
        {
            var id = DocumentRender.ClaimKeyFor(resultKey, generation);
            try
            {
                session.Insert(new DocumentRender { Id = id, ClaimedAt = DateTimeOffset.UtcNow });
                await session.SaveChangesAsync(ct);
                return new Claim(id, null);
            }
            catch (DocumentAlreadyExistsException)
            {
                session.EjectAllPendingChanges();
            }

            if (await session.LoadAsync<DocumentRender>(resultKey, ct) is { } done)
            {
                return new Claim(null, done.FileId);
            }

            var held = await session.LoadAsync<DocumentRender>(id, ct);
            if (held is null) continue;
            if (DateTimeOffset.UtcNow - held.ClaimedAt <= ClaimLifetime) return Claim.None;
            generation++;
        }

        return Claim.None;
    }

    private async Task<WorkflowActionResult> StoreAsync(
        byte[] pdf, string name, string? renderId, string? claimId, CancellationToken ct)
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
            await ReleaseAsync(claimId);
            throw;
        }

        if (saved.File is null)
        {
            await ReleaseAsync(claimId);
            return saved.CanRetry
                ? WorkflowActionResult.Failure(saved.Refused ?? "The file store did not take the document.")
                : WorkflowActionResult.PermanentFailure(saved.Refused ?? "The file store refused the document.");
        }

        if (renderId is null) return Produced(saved.File.Id);

        // The result is inserted, not stored: if a slower or faster run finished first, this one
        // returns that run's file instead of replacing its record. The claim goes in the same commit.
        try
        {
            session.Insert(new DocumentRender { Id = renderId, FileId = saved.File.Id });
            if (claimId is not null) session.Delete<DocumentRender>(claimId);
            await session.SaveChangesAsync(ct);
            return Produced(saved.File.Id);
        }
        catch (DocumentAlreadyExistsException)
        {
            session.EjectAllPendingChanges();
            await ReleaseAsync(claimId);
            var first = await session.LoadAsync<DocumentRender>(renderId, ct);
            return first is null
                ? WorkflowActionResult.Failure("Another run finished this document first. Try again.")
                : Produced(first.FileId);
        }
    }

    private async Task ReleaseAsync(string? claimId)
    {
        if (claimId is null) return;
        session.EjectAllPendingChanges();
        session.Delete<DocumentRender>(claimId);
        await session.SaveChangesAsync(CancellationToken.None);
    }

    internal static string FileNameFor(DocumentTemplate template, Content content)
    {
        var name = Clean(string.IsNullOrWhiteSpace(template.FileName)
            ? template.Slug
            : DocumentValues.Fill(template.FileName, content));
        if (name.Length == 0) name = Clean(template.Slug);
        if (name.Length == 0) name = "document";
        if (name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name = name[..^".pdf".Length];
        if (name.Length > MaxNameCharacters) name = name[..MaxNameCharacters];
        return name + ".pdf";
    }

    private static string Clean(string name)
    {
        name = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
        return new string(name.Where(c => !char.IsControl(c) && !InvalidNameCharacters.Contains(c)).ToArray()).Trim();
    }

    private static WorkflowActionResult Produced(Guid fileId) =>
        WorkflowActionResult.Success(new Dictionary<string, string> { ["FileId"] = fileId.ToString() });
}
