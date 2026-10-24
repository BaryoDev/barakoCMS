using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Attributes;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;
using Microsoft.AspNetCore.Http;

namespace barakoCMS.Features.Workflows.Actions;

/// <summary>
/// Workflow action that creates a numbered entry the way <c>POST /api/contents</c> does.
/// </summary>
/// <remarks>
/// The entry goes through <see cref="IContentCreator"/>, so it faces the type's save rules, its
/// schema validation, its lifecycle hooks, its numbering and its initial state, and the same
/// write-side field sensitivity as an HTTP create.
///
/// It runs as the system, so it skips what depends on who is asking: the caller's Create
/// permission and the caller's field sets. The author of the workflow chose the type and the
/// values, and no user stands behind the run. Sensitivity is asked of a caller with no roles, so a
/// field above Public is dropped rather than written, which keeps a workflow from filling a field
/// no ordinary caller may see. The entry is created by the empty user id, as other writes with no
/// user are.
///
/// Retry safe. The new entry's id comes from the attempt's <c>IdempotencyKey</c>, which is the same
/// on every rerun of one attempt. The run's own attempt record is committed apart from this
/// action's write, so a rerun after the write but before the record finds the entry under that id
/// and reports it again instead of creating a second one. A rule or validation refusal is
/// permanent: the same values are refused on every retry.
/// </remarks>
[WorkflowActionMetadata(
    Description = "Creates an entry with numbering, initial state and rules.",
    Group = WorkflowActionGroup.Content,
    RequiredParameters = new[] { "ContentType" },
    OptionalParameters = new[] { "Id", "Data.<field>", "BranchFrom" },
    ExampleJson = @"{""Type"":""CreateContent"",""Parameters"":{""ContentType"":""Task"",""Data.OrderId"":""{{Id}}""},""Capture"":[""id""]}")]
internal sealed class CreateContentAction(
    IDocumentSession session, IContentCreator creator, IEventEnvelopeContext envelope,
    barakoCMS.Infrastructure.Envelope.IBranchCodes branchCodes) : IWorkflowAction
{
    public string Type => "CreateContent";

    /// <inheritdoc />
    public async Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
        => await RunAsync(parameters, content, ct);

    /// <inheritdoc />
    public async Task<WorkflowActionResult> RunAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct)
    {
        if (!parameters.TryGetValue("ContentType", out var type) || string.IsNullOrWhiteSpace(type))
            return WorkflowActionResult.PermanentFailure("ContentType is required.");

        var id = ResolveId(parameters);
        var runId = Guid.TryParse(parameters.GetValueOrDefault("RunId"), out var parsedRun) ? parsedRun : (Guid?)null;

        // The new entry takes the branch of the entry that triggered the run, unless told not to.
        var branch = parameters.GetValueOrDefault("BranchFrom", "subject") == "none" ? null : content.BranchId;

        if (await session.LoadAsync<barakoCMS.Models.Content>(id, ct) is { } existing)
        {
            // Done only when the entry is the one an earlier attempt of this action made. An id from
            // a template can name any entry, and reporting a stranger's as created would hand its
            // number and id on to the next action.
            return barakoCMS.Core.ContentTypeName.Same(existing.ContentType, type)
                && existing.BranchId == branch && existing.SourceRunId == runId
                ? await DoneAsync(existing, type, ct)
                : WorkflowActionResult.PermanentFailure("An entry with that id exists and was not made by this action.");
        }

        var request = new ContentCreateRequest
        {
            ContentType = type,
            Id = id,
            Data = parameters
                .Where(kv => kv.Key.StartsWith("Data.", StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key[5..], kv => (object)kv.Value),
            // Always a draft. A Status parameter is not read: the HTTP create asks nothing of the status
            // it is sent, and a workflow must not publish what no user chose to publish.
            Status = ContentStatus.Draft,
            SourceRunId = runId,
        };

        // A json field takes only a whole list or object, which a template fills as JSON text. A
        // value mixed into other text could add keys, so that is refused. Permanent, no value named.
        var declared = (await barakoCMS.Infrastructure.Services.AccountContentTypes.ResolveAsync(session, type, ct))?.Fields;
        var refusal = barakoCMS.Core.Validation.JsonTextFields.Read(
            declared, request.Data, key => ActionParameters.IsStructured(parameters, "Data." + key));
        if (refusal is not null)
            return WorkflowActionResult.PermanentFailure(refusal);

        // The code goes with the id: a {branch} number series reads it, as it does on the HTTP path.
        var code = branch is { } branchId ? await branchCodes.CodeOfAsync(branchId, ct) : null;
        using var _ = envelope.Use(branch, code);

        var errors = await creator.CheckAsync(request, Guid.Empty, new DefaultHttpContext(), batch: null, ct);
        if (errors.Count > 0)
            return WorkflowActionResult.PermanentFailure(request.RuleFailure ?? string.Join(" ", errors));

        barakoCMS.Models.Content created;
        try
        {
            created = await creator.StageAsync(request, Guid.Empty, batch: null, ct);
        }
        catch (ContentCreateRefusedException refused)
        {
            return WorkflowActionResult.PermanentFailure(refused.Message);
        }

        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (Marten.Exceptions.ExistingStreamIdCollisionException)
        {
            // Another node ran this attempt first. The next try finds its entry and reports it.
            return WorkflowActionResult.Failure("The entry was created by another run of this attempt.");
        }

        return await DoneAsync(created, type, ct);
    }

    private static Guid ResolveId(Dictionary<string, string> parameters) =>
        Guid.TryParse(parameters.GetValueOrDefault("Id"), out var given) ? given
        : parameters.TryGetValue("IdempotencyKey", out var key) ? DeterministicId.From(key)
        : Guid.CreateVersion7();

    private async Task<WorkflowActionResult> DoneAsync(barakoCMS.Models.Content created, string type, CancellationToken ct)
    {
        var definition = await barakoCMS.Infrastructure.Services.AccountContentTypes.ResolveAsync(session, type, ct);
        var number = definition?.Numbering?.Field is { Length: > 0 } field
            && created.Data.TryGetValue(field, out var value) ? value?.ToString() : null;

        var outputs = new Dictionary<string, string> { ["id"] = created.Id.ToString(), ["contentType"] = type };
        if (number is not null) outputs["number"] = number;
        return WorkflowActionResult.Success(outputs, [new CreatedRef(type, created.Id, number)]);
    }
}
