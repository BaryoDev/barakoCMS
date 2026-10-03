using System.Text.Json;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using Marten;
using Microsoft.AspNetCore.Http;

namespace barakoCMS.Infrastructure.Services;

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
    IContentWriter writer) : IContentCreator
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

        bool isValid;
        List<string> errors;
        using (barakoCMS.Core.Validation.ReferenceFields.Expecting(batch?.Expected))
        {
            (isValid, errors) = schema is null
                ? await validator.ValidateAsync(request.ContentType, request.Data, existing: null)
                : await validator.ValidateFieldsAsync(schema, request.ContentType, request.Data, existing: null);
        }

        string? slug = null;
        if (isValid && schema is not null && batch is not null)
        {
            if (batch.CapSingletons && schema.IsSingleton && await HoldsAnEntryAsync(schema, batch, ct))
            {
                errors.Add($"'{schema.DisplayName}' holds a single entry and already has one. "
                         + "Edit that entry rather than creating another.");
            }

            slug = SlugOf(schema, request.Data);
            if (slug is not null && batch.SlugTaken(request.ContentType, slug))
            {
                errors.Add($"'{slug}' is used by another '{request.ContentType}' entry in this batch, "
                         + "and a slug has to name one entry.");
            }
        }

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

        batch?.Accept(request.ContentType, slug);
        return [];
    }

    public async Task<Content> StageAsync(
        ContentCreateRequest request, Guid userId, ContentCreateBatch? batch, CancellationToken ct)
    {
        var definition = batch?.SchemaFor(request.ContentType)
            ?? await session.Query<ContentTypeDefinition>()
                .FirstOrDefaultAsync(d => d.Name == request.ContentType, ct);

        var publicFields = definition?.Fields
            .Where(f => f.Sensitivity == SensitivityLevel.Public)
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
            request.Id ?? Guid.NewGuid(), request.ContentType, request.Data, request.Status, userId, searchText,
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

        return created;
    }

    private async Task<bool> HoldsAnEntryAsync(ContentTypeDefinition schema, ContentCreateBatch batch, CancellationToken ct)
    {
        if (batch.AcceptedOf(schema.Name) > 0)
            return true;

        var typeName = schema.Name.ToLower();
        return await session.Query<Content>().AnyAsync(c => c.ContentType.ToLower() == typeName, ct);
    }

    private static string? SlugOf(ContentTypeDefinition schema, Dictionary<string, object> data)
    {
        var slugField = Features.Public.PublicDelivery.SlugField(schema);
        if (slugField is null)
            return null;

        var submitted = data.FirstOrDefault(kv => kv.Key.Equals(slugField, StringComparison.OrdinalIgnoreCase));
        var slug = submitted.Value is JsonElement je ? je.ToString() : submitted.Value?.ToString();
        return string.IsNullOrWhiteSpace(slug) ? null : slug;
    }
}
