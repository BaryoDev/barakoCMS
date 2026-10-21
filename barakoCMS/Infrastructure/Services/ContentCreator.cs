using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using Marten;
using Marten.Linq.MatchesSql;
using Microsoft.AspNetCore.Http;

namespace barakoCMS.Infrastructure.Services;

/// <summary>A create the server refuses for a reason the caller can fix, such as a branch with no code. Callers answer 400.</summary>
public sealed class ContentCreateRefusedException(string message) : Exception(message);

/// <summary>One entry to create, as the caller asked for it.</summary>
public sealed class ContentCreateRequest
{
    public required string ContentType { get; init; }

    /// <summary>The field values. Checking may drop fields from it and hooks may add to it.</summary>
    public required Dictionary<string, object> Data { get; init; }

    public ContentStatus Status { get; init; } = ContentStatus.Draft;
    public SensitivityLevel Sensitivity { get; init; } = SensitivityLevel.Public;

    /// <summary>
    /// The new entry's id, or null for a fresh one. A batch sets it when an entry later in the batch
    /// has to reference this one before it exists.
    /// </summary>
    public Guid? Id { get; init; }

    /// <summary>The workflow run creating the entry, or null. Set by server code only; no request body carries it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Guid? SourceRunId { get; init; }

    /// <summary>The refusal of a save rule, when one refused the entry. Callers answer it as 422.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? RuleFailure { get; set; }

    /// <summary>
    /// Whether the caller may create the entry, asked of the data once write sensitivity has dropped
    /// what the caller may not write and before any save rule runs. Null asks nothing.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<ContentCreateRequest, CancellationToken, Task<bool>>? Permitted { get; init; }

    /// <summary>Set when <see cref="Permitted"/> refused the entry. Callers answer it as 403.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Forbidden { get; set; }
}

/// <summary>
/// Several creates checked and staged as one unit, for a caller that writes a type and its entries
/// together.
/// </summary>
/// <remarks>
/// A batch carries the schema each entry is to be written under, which may be a type the same unit
/// creates or changes, and remembers what earlier entries claimed. Run it under
/// <see cref="IContentBatchRunner"/> so each entry is written before the next is checked, and the
/// validator and lifecycle hooks read the entries before it.
/// </remarks>
public sealed class ContentCreateBatch
{
    private readonly Dictionary<string, ContentTypeDefinition> _schemas = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _accepted = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(string Type, string Slug)> _slugs = new();
    private readonly Dictionary<Guid, string> _expected = new();

    /// <summary>
    /// Whether a singleton type may hold only one entry across the store and this batch. True by
    /// default. A restore turns it off, because a bundle lands what it holds.
    /// </summary>
    public bool CapSingletons { get; init; } = true;

    /// <summary>
    /// Asked of each entry once its checks and hooks have passed and before the batch counts it,
    /// with the data as it will be stored. A message refuses the entry with that message, and the
    /// entry then claims no slug and no singleton place. Null by default, which admits every entry.
    /// </summary>
    public Func<ContentCreateRequest, CancellationToken, Task<string?>>? Admit { get; init; }

    /// <summary>Checks entries of <paramref name="definition"/>'s name against it rather than the store.</summary>
    public void UseSchema(ContentTypeDefinition definition) => _schemas[definition.Name] = definition;

    /// <summary>
    /// Says this batch will create an entry of <paramref name="contentType"/> under
    /// <paramref name="id"/>, so a reference list checked before it is written may name it.
    /// </summary>
    /// <remarks>
    /// For entries that list each other, which no write order can satisfy one at a time. Only a
    /// list reference reads this; a single reference still needs its target written first. The
    /// caller refuses the whole batch when any entry is refused, so an id expected here and then not
    /// written is never committed.
    /// </remarks>
    public void Expect(Guid id, string contentType) => _expected[id] = contentType;

    internal IReadOnlyDictionary<Guid, string> Expected => _expected;

    internal ContentTypeDefinition? SchemaFor(string contentType) =>
        _schemas.TryGetValue(contentType, out var schema) ? schema : null;

    internal int AcceptedOf(string contentType) => _accepted.GetValueOrDefault(contentType);

    internal bool SlugTaken(string contentType, string slug) =>
        _slugs.Contains((contentType.ToLowerInvariant(), slug.ToLowerInvariant()));

    internal void Accept(string contentType, string? slug)
    {
        _accepted[contentType] = AcceptedOf(contentType) + 1;
        if (slug is not null)
            _slugs.Add((contentType.ToLowerInvariant(), slug.ToLowerInvariant()));
    }
}

/// <summary>
/// The content create write path: what <c>POST /api/contents</c> does to an entry, for every route
/// that creates one.
/// </summary>
/// <remarks>
/// Split in two so a caller writing many entries can check all of them before staging any, and
/// refuse the whole unit without having written part of it. A single create calls both in turn.
/// </remarks>
public interface IContentCreator
{
    /// <summary>
    /// Runs create's checks in create's order: write-side sensitivity keyed on the caller, which
    /// drops from <see cref="ContentCreateRequest.Data"/> any field the caller may not see; schema
    /// validation; then the type's lifecycle hooks, which may add to the data and only run once
    /// validation passed. Returns every error; empty means the entry may be staged.
    /// </summary>
    Task<IReadOnlyList<string>> CheckAsync(
        ContentCreateRequest request, Guid userId, HttpContext httpContext, ContentCreateBatch? batch, CancellationToken ct);

    /// <summary>
    /// Stages the create into the current session: the event, the document and the type's initial
    /// lifecycle state. Does not commit; the caller owns the transaction.
    /// </summary>
    Task<Content> StageAsync(ContentCreateRequest request, Guid userId, ContentCreateBatch? batch, CancellationToken ct);
}

public sealed class ContentCreator(
    IDocumentSession session,
    ISensitivityService sensitivity,
    IContentValidatorService validator,
    IContentLifecycleRunner lifecycle,
    IContentWriter writer,
    IRuleEngine rules,
    INumberStrategies? numbering = null,
    IEventEnvelopeContext? envelope = null,
    IExternalNumberLedger? ledger = null) : IContentCreator
{
    public async Task<IReadOnlyList<string>> CheckAsync(
        ContentCreateRequest request, Guid userId, HttpContext httpContext, ContentCreateBatch? batch, CancellationToken ct)
    {
        var schema = batch?.SchemaFor(request.ContentType);

        // WRITE-PATH SENSITIVITY: drop any sensitive fields this caller may not see, so they cannot
        // inject values into fields that would be masked from them on read.
        if (schema is null)
            await sensitivity.ApplyWriteAsync(request.ContentType, request.Data, existing: null, httpContext, ct);
        else
            await sensitivity.ApplyWriteAsync(schema, request.Data, existing: null, httpContext, ct);

        // Asked of what the caller may write, so a field sensitivity drops cannot pass it, and before
        // the rules, so its answer does not depend on what a rule read.
        if (request.Permitted is { } permitted && !await permitted(request, ct))
        {
            request.Forbidden = true;
            return ["Forbidden."];
        }

        // Rules before the schema, so a field a rule writes is validated and seen by the hooks. A
        // batch's gate answers first, so a caller it refuses learns nothing a rule read.
        if (await RuleRefusalAsync(request, userId, ct) is { } refused)
            return [batch?.Admit is { } gate && await gate(request, ct) is { } denied ? denied : refused];

        bool isValid;
        List<string> errors;
        using (barakoCMS.Core.Validation.ReferenceFields.Expecting(batch?.Expected))
        {
            (isValid, errors) = schema is null
                ? await validator.ValidateAsync(request.ContentType, request.Data, existing: null, caller: httpContext.User)
                : await validator.ValidateFieldsAsync(schema, request.ContentType, request.Data, existing: null, caller: httpContext.User);
        }

        // The validator refuses a write while a field change runs on the type; the batch path does not run that check.
        if (isValid && schema is not null
            && await barakoCMS.Infrastructure.Jobs.FieldChanges.RunningAsync(session, request.ContentType, ct) is { } running)
        {
            errors.Add(barakoCMS.Infrastructure.Jobs.FieldChanges.Busy(running));
        }

        if (await NeedsBranchAsync(schema, request.ContentType, ct))
            errors.Add("A branch is required.");

        var slug = isValid && schema is not null && batch is not null
            ? await BatchClashesAsync(schema, request, batch, errors, ct)
            : null;

        if (errors.Count > 0)
            return errors;

        // DOMAIN RULES. Schema validation answers "is this the right shape"; a module's lifecycle hook
        // answers "is this legal" (e.g. a journal entry's debits must equal its credits) and may
        // enrich the entry (e.g. stamp the next sequence number). Runs after validation so a hook can
        // trust the field types it reads.
        var hookErrors = await lifecycle.RunBeforeSaveAsync(
            request.ContentType, entryId: null, request.Data, existing: null, userId, ct);
        if (hookErrors.Count > 0)
            return hookErrors;

        if (batch?.Admit is { } admit && await admit(request, ct) is { } refusal)
            return [refusal];

        batch?.Accept(request.ContentType, slug);
        return [];
    }

    /// <summary>
    /// The checks only a batch needs: a singleton that already holds an entry, and a slug another
    /// entry of the batch took. Returns the entry's slug.
    /// </summary>
    private async Task<string?> BatchClashesAsync(
        ContentTypeDefinition schema, ContentCreateRequest request, ContentCreateBatch batch, List<string> errors, CancellationToken ct)
    {
        if (batch.CapSingletons && schema.IsSingleton && await HoldsAnEntryAsync(schema, batch, ct))
        {
            errors.Add($"'{schema.DisplayName}' holds a single entry and already has one. "
                     + "Edit that entry rather than creating another.");
        }

        var slug = SlugOf(schema, request.Data);
        if (slug is not null && batch.SlugTaken(request.ContentType, slug))
        {
            errors.Add($"'{slug}' is used by another '{request.ContentType}' entry in this batch, "
                     + "and a slug has to name one entry.");
        }

        return slug;
    }

    /// <summary>
    /// Runs the type's save rules against the new entry. On a pass the fields a rule wrote go into
    /// the request's data; on a refusal the message is returned and kept on the request.
    /// </summary>
    private async Task<string?> RuleRefusalAsync(ContentCreateRequest request, Guid userId, CancellationToken ct)
    {
        var working = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in request.Data) working[key] = value;
        var before = new Dictionary<string, object?>(working, StringComparer.OrdinalIgnoreCase);

        var result = await rules.EvaluateAsync(RuleWhen.Save,
            new RuleContext(request.ContentType, request.Id, working, null, null, null, null, userId, session), ct);
        if (!result.Passed)
            return request.RuleFailure = result.Message ?? "A rule failed.";

        foreach (var (key, value) in working)
        {
            if (value is not null && (!before.TryGetValue(key, out var old) || !Equals(old, value)))
                request.Data[request.Data.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) ?? key] = value;
        }

        return null;
    }

    /// <summary>A branch scoped type written with no branch in the envelope.</summary>
    /// <remarks>The single create passes no batch and so no schema; the stored type answers then.</remarks>
    private async Task<bool> NeedsBranchAsync(ContentTypeDefinition? schema, string contentType, CancellationToken ct) =>
        Envelope.EventEnvelopeContext.CurrentBranch is null
        && (schema?.BranchScoped ?? await session.Query<ContentTypeDefinition>()
            .Where(barakoCMS.Core.ContentTypeName.Named(contentType))
            .AnyAsync(d => d.BranchScoped, ct));

    public async Task<Content> StageAsync(
        ContentCreateRequest request, Guid userId, ContentCreateBatch? batch, CancellationToken ct)
    {
        var definition = batch?.SchemaFor(request.ContentType)
            ?? await session.Query<ContentTypeDefinition>()
                .FirstNamedAsync(request.ContentType, ct);

        var entryId = request.Id ?? Guid.NewGuid();
        await NumberAsync(definition, request.ContentType, entryId, request.Data, ct);

        var publicFields = definition?.Fields
            .Where(f => f.Sensitivity == SensitivityLevel.Public && !barakoCMS.Core.Validation.InlineImageFields.Is(f.Type))
            .Select(f => f.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var searchText = string.Join(
            ' ',
            request.Data
                .Where(kv => publicFields.Contains(kv.Key))
                .Select(kv => kv.Value?.ToString())
                .Where(v => !string.IsNullOrWhiteSpace(v)));

        // Stored as the caller spelled it, deliberately, and the rebuild is what compares
        // case-insensitively. Normalising here looks like the tidier fix and is not: the type name
        // lands on the Content document via the projection, and modules match it exactly.
        // BarakoCMS.Accounting queries ContentType == "journalEntry", so lowercasing the write turned
        // every ledger and trial balance into zero rows, silently, with the postings still in place.
        var @event = new Events.ContentCreated(
            entryId, request.ContentType, request.Data, request.Status, userId, searchText,
            request.Sensitivity, DateTime.UtcNow);

        var created = await writer.CreateAsync(@event, ct);

        // A type with its own lifecycle starts its entries at the state it declared. Set on the
        // document rather than carried in ContentCreated, because the event is public API under
        // section 6 and this can be derived from the type definition at any time, including on a
        // replay. Null stays null for every type that declares no lifecycle.
        if (definition?.Lifecycle is { } declared)
        {
            created.LifecycleState = declared.InitialState;
            session.Store(created);
        }

        // On the document for the same reason: ContentCreated is public API and unchanged.
        if (request.SourceRunId is { } run)
        {
            created.SourceRunId = run;
            session.Store(created);
        }

        return created;
    }

    /// <summary>
    /// Writes the next number of the type's series into the field its rule names, replacing whatever
    /// the request sent there: the server owns it.
    /// </summary>
    /// <remarks>
    /// Here rather than in the checks, so a request refused, or a dry run, takes no number. A gapless
    /// number is taken inside this session's transaction and commits or rolls back with the entry.
    /// </remarks>
    /// <remarks>
    /// The type is matched ignoring case here even where the create's own lookup is exact, so an
    /// entry created as INVOICE under a type named invoice is still numbered.
    /// </remarks>
    private async Task NumberAsync(ContentTypeDefinition? definition, string contentType, Guid entryId, Dictionary<string, object> data, CancellationToken ct)
    {
        if (numbering is null)
        {
            return;
        }

        var lowered = contentType.ToLower();
        definition ??= await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name.ToLower() == lowered, ct);
        if (definition?.Numbering is not { Field.Length: > 0 } rule)
        {
            return;
        }

        var sent = data.FirstOrDefault(p => string.Equals(p.Key, rule.Field, StringComparison.OrdinalIgnoreCase)).Value?.ToString();
        foreach (var key in data.Keys.Where(k => string.Equals(k, rule.Field, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            data.Remove(key);
        }

        NumberSeries series;
        try
        {
            series = barakoCMS.Core.Validation.NumberingRules.SeriesFor(definition, rule, envelope?.BranchCode);
        }
        catch (InvalidOperationException)
        {
            throw new ContentCreateRefusedException("The numbering needs a branch with a code.");
        }

        if (rule.ClientAllocated && !string.IsNullOrWhiteSpace(sent) && ledger is not null)
        {
            await KeepClientNumberAsync(series, rule, contentType, entryId, sent, data, ct);
            return;
        }

        data[rule.Field] = (await numbering.For(rule.Strategy).NextAsync(session, series, ct)).Formatted;
    }

    /// <summary>
    /// Keeps the number a client took from the next-number route, and records it in the ledger so the
    /// gaps report counts it. A number that is not of the series, or that an entry already holds, is refused.
    /// </summary>
    private async Task KeepClientNumberAsync(
        NumberSeries series, NumberingRule rule, string contentType, Guid entryId, string sent, Dictionary<string, object> data, CancellationToken ct)
    {
        var field = rule.Field;
        if (!series.TryParse(sent, out var value))
        {
            throw new ContentCreateRefusedException("That number does not fit the series.");
        }

        // One spelling per number: P-0001, P-00001 and P-1 are the one number 1, so only the form the
        // series formats is kept. The ledger key and the text check below then read the same number.
        if (!string.Equals(sent, series.Format(value, series.PeriodFor(DateTimeOffset.UtcNow)), StringComparison.Ordinal))
        {
            throw new ContentCreateRefusedException("That number does not fit the series.");
        }

        // Only a number the next-number route has handed out is kept. Without this a client could
        // take a number ahead of the counter, and the route would later hand it out again.
        if (value > await HandedOutAsync(series, ct))
        {
            throw new ContentCreateRefusedException("That number was not handed out.");
        }

        // Two branches count apart, so each may issue its own P-0001. Only the branch's own entries hold a number against it.
        // A branch series names the branch in its key, so a write with no branch id fails closed rather than reading another's.
        var lowered = contentType.ToLower();
        var held = session.Query<Content>().Where(c => c.ContentType.ToLower() == lowered);
        if (barakoCMS.Core.Validation.NumberingRules.NeedsBranch(rule))
        {
            if (envelope?.BranchId is not { } branch)
            {
                throw new ContentCreateRefusedException("The numbering needs a branch with a code.");
            }

            held = held.Where(c => c.BranchId == branch);
        }

        // The ledger holds the number under the series name (branch code included), so it answers for
        // entries of this unit of work not yet saved as well as for older ones.
        var useId = barakoCMS.Infrastructure.Numbering.ExternalNumberLedger.IdFor(session.TenantId, series.Name, value);
        if (session.PendingChanges.Inserts().OfType<ExternalNumberUse>().Any(u => u.Id == useId)
            || await session.LoadAsync<ExternalNumberUse>(useId, ct) is not null
            || await held.Where(c => c.MatchesSql(SentNumberSql, field, sent)).AnyAsync(ct))
        {
            throw new ContentCreateRefusedException("That number is already issued.");
        }

        ledger!.Record(session, series.Name, value, entryId.ToString("N"));
        data[field] = sent;
    }

    /// <summary>The last number the series' counter handed out, 0 when it has handed out none.</summary>
    private async Task<long> HandedOutAsync(NumberSeries series, CancellationToken ct)
    {
        // A client-allocated series cannot reset, so its one period is the one any instant falls in.
        var schema = session.DocumentStore.Options.DatabaseSchemaName;
        var rows = await session.QueryAsync<long>(
            $"select last_value from {schema}.{barakoCMS.Infrastructure.Numbering.NumberCounterTable.Name} where tenant_id = ? and series = ? and period = ?",
            ct, session.TenantId, series.Name, series.PeriodFor(DateTimeOffset.UtcNow));
        return rows.Count > 0 ? rows[0] : 0;
    }

    private const string SentNumberSql =
        "(SELECT e.value #>> '{}' FROM jsonb_each(d.data -> 'Data') e WHERE lower(e.key) = lower(?) LIMIT 1) = ?";

    private async Task<bool> HoldsAnEntryAsync(ContentTypeDefinition schema, ContentCreateBatch batch, CancellationToken ct)
    {
        if (batch.AcceptedOf(schema.Name) > 0)
            return true;

        var typeName = schema.Name.ToLower();
        return await session.Query<Content>().AnyAsync(c => c.ContentType.ToLower() == typeName, ct);
    }

    private static string? SlugOf(ContentTypeDefinition schema, Dictionary<string, object> data)
    {
        var slugField = Features.Public.PublicDelivery.SlugFieldForAuthoring(schema);
        if (slugField is null)
            return null;

        var submitted = data.FirstOrDefault(kv => kv.Key.Equals(slugField, StringComparison.OrdinalIgnoreCase));
        var slug = submitted.Value is JsonElement je ? je.ToString() : submitted.Value?.ToString();
        return string.IsNullOrWhiteSpace(slug) ? null : slug;
    }
}
