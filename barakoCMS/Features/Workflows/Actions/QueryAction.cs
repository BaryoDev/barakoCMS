using System.Globalization;
using System.Text.Json;
using barakoCMS.Infrastructure;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Features.Workflows.Actions;

/// <summary>
/// Reads entries with a saved query and hands the first row's fields to the steps after it.
/// </summary>
/// <remarks>
/// <c>{ "Type": "Query", "Parameters": { "Query": "student-by-number" }, "Capture": ["Email"] }</c>,
/// then a later step reads <c>{{actions.0.Email}}</c>. The query is the saved one, so what it reads
/// is reviewed where queries are written and not in each workflow.
///
/// The query's parameters are bound as values from the entry that fired the run
/// (<c>@id</c>, <c>@createdAt</c>, <c>@data.Field</c>) and from earlier steps
/// (<c>@actions.N.name</c>). Nothing in an entry is read as query text. The slug is not resolved
/// against the entry either, so entry data cannot choose which query runs.
///
/// Every read is made as the user behind the event that fired the run, the same way references are
/// followed, and goes through the permission and sensitivity services a read of one entry uses. With
/// nobody behind the event only Public fields are read. A field the user may not read fails the step
/// with a message, and is never dropped.
///
/// The attempt records how many rows the query returned, never a value and no entry id. Values,
/// and an id if the query selects <c>id</c>, are kept only as an output the workflow captured,
/// which is how every output is kept.
/// </remarks>
[barakoCMS.Infrastructure.Attributes.WorkflowActionMetadata(
    Description = "Read entries with a saved query and pass the first row's fields to later steps",
    Group = barakoCMS.Infrastructure.Attributes.WorkflowActionGroup.Data,
    RequiredParameters = new[] { "Query" },
    SelfResolvedParameters = new[] { "Query" },
    ExampleJson = @"{""Type"":""Query"",""Parameters"":{""Query"":""student-by-number""},""Capture"":[""Email"",""Name""]}"
)]
internal sealed class QueryAction(
    IQuerySession session,
    IContextQueryRunner runner,
    ITemplateVariableExtractor variables,
    ILogger<QueryAction> logger) : IWorkflowAction
{
    public const string SlugParameter = "Query";

    public string Type => "Query";

    [Obsolete("Implement RunAsync instead.")]
    public Task ExecuteAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct) =>
        throw new NotSupportedException("A Query step runs with its context, so it reads as the user behind the run.");

    public Task<WorkflowActionResult> RunAsync(Dictionary<string, string> parameters, barakoCMS.Models.Content content, CancellationToken ct) =>
        Task.FromResult(WorkflowActionResult.PermanentFailure("A Query step runs with its context, so it reads as the user behind the run."));

    public async Task<WorkflowActionResult> RunAsync(WorkflowContext context, CancellationToken ct)
    {
        var slug = context.Parameters.GetValueOrDefault(SlugParameter);
        if (string.IsNullOrWhiteSpace(slug))
        {
            return WorkflowActionResult.PermanentFailure("No 'Query' parameter names a saved query.");
        }

        if (!Slugs.IsValid(slug))
        {
            return WorkflowActionResult.PermanentFailure("The 'Query' parameter is not a saved query name. A name is lowercase letters, digits and hyphens.");
        }

        // One tenant for the whole step. The session is the run's own, so this holds unless a host
        // wired it otherwise, and then nothing is read.
        if (!string.Equals(TenantScopes.SlugFor(session.TenantId), context.TenantId, StringComparison.Ordinal))
        {
            return WorkflowActionResult.PermanentFailure("The query was not run, because this step was not given the tenant of its run.");
        }

        var definition = await session.Query<QueryDefinition>()
            .FirstOrDefaultAsync(q => q.Slug == slug && q.DeletedAt == null, ct);
        if (definition is null)
        {
            return WorkflowActionResult.PermanentFailure($"The saved query '{slug}' does not exist.");
        }

        var (values, problem) = Bind(definition, context);
        if (problem is not null)
        {
            return WorkflowActionResult.Failure($"Query '{slug}': {problem}");
        }

        var actor = variables is IWorkflowActor source
            ? await source.ActorOfAsync(context.Content, context.TriggerEvent, context.EventSequence, ct)
            : Guid.Empty;

        QueryResult result;
        try
        {
            result = await runner.RunAsync(
                definition, new QueryRunContext(values) { AsActor = true, Actor = actor }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The type only. The text of an exception can carry the values the query was given.
            logger.LogWarning("Query step '{Slug}' failed with {Error}", slug, ex.GetType().Name);
            return WorkflowActionResult.Failure($"Query '{slug}' could not be run: {ex.GetType().Name}.");
        }

        if (!result.Ok)
        {
            // A refusal is the same answer when asked again, so it is not retried. A run that took too
            // long may not be, so that one is.
            var refused = $"Query '{slug}': {result.Refusal}";
            return result.TimedOut ? WorkflowActionResult.Failure(refused) : WorkflowActionResult.PermanentFailure(refused);
        }

        var names = new List<string>();
        foreach (var field in definition.Fields)
        {
            var name = field.Replace('.', '_');
            if (!ActionOutputs.IsValidName(name))
            {
                return WorkflowActionResult.PermanentFailure($"Query '{slug}' selects '{field}', which cannot be an output name.");
            }

            if (names.Contains(name, StringComparer.Ordinal))
            {
                return WorkflowActionResult.PermanentFailure($"Query '{slug}' selects two fields that would both be the output '{name}'.");
            }

            names.Add(name);
        }

        if (names.Count > ActionOutputs.MaxCaptured)
        {
            return WorkflowActionResult.PermanentFailure(
                $"Query '{slug}' selects {names.Count} fields, and a step may hand on at most {ActionOutputs.MaxCaptured}.");
        }

        var first = result.Rows.Count > 0 ? result.Rows[0] : null;
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < names.Count; i++)
        {
            var text = first is not null && first.TryGetValue(definition.Fields[i], out var value) ? Text(value) : string.Empty;
            if (text.Length > ActionOutputs.MaxValueLength)
            {
                // Failed and not cut: a value that is not the whole value would go on into a message.
                return WorkflowActionResult.PermanentFailure(
                    $"Query '{slug}': the value of '{definition.Fields[i]}' is longer than the {ActionOutputs.MaxValueLength} characters a step may hand on.");
            }

            outputs[names[i]] = text;
        }

        return WorkflowActionResult.Success(outputs) with { Note = Note(slug, result) };
    }

    /// <summary>What the record says about the rows: how many came back. Never a value, and no id.</summary>
    /// <remarks>
    /// The count is the rows the query returned, so a limit caps it. The ids are not listed: the
    /// runner stores an attempt's error through the credential redaction, which takes out anything
    /// shaped like a GUID, and a run viewer may not read the type the entries belong to.
    /// </remarks>
    internal static string Note(string slug, QueryResult result)
    {
        if (result.Count == 0) return $"Query '{slug}' returned no entries, so its outputs are empty.";

        return $"Query '{slug}' returned {result.Count} {(result.Count == 1 ? "entry" : "entries")}."
            + (result.Count > 1 ? " The first row was kept." : string.Empty);
    }

    /// <summary>The value of each parameter the query's filters name, or why one cannot be given.</summary>
    private static (Dictionary<string, string>? Values, string? Problem) Bind(QueryDefinition definition, WorkflowContext context)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in definition.Filters.Select(f => f.Param).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.Ordinal))
        {
            var value = Value(name!, context);
            if (value is not null) values[name!] = value;
        }

        return (values, null);
    }

    private static string? Value(string name, WorkflowContext context)
    {
        if (name == "id") return context.Content.Id.ToString();
        if (name == "createdAt") return context.Content.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

        var parts = name.Split('.');
        if (parts is ["data", var field])
        {
            if (context.Content.Data is null) return null;
            foreach (var (key, value) in context.Content.Data)
            {
                if (string.Equals(key, field, StringComparison.OrdinalIgnoreCase)) return value is null ? null : Text(value);
            }

            return null;
        }

        if (parts is ["actions", var step, var output] && int.TryParse(step, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal))
        {
            return context.Outputs.TryGetValue(ordinal, out var kept) && kept.TryGetValue(output, out var text) ? text : null;
        }

        return null;
    }

    /// <summary>A stored value as the text an output holds. Numbers and dates are written the same way for every reader.</summary>
    private static string Text(object value) => value switch
    {
        null => string.Empty,
        string s => s,
        bool b => b ? "true" : "false",
        DateTime d => d.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? string.Empty,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => JsonSerializer.Serialize(value),
    };
}
