using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using FluentValidation.Results;
using Marten;
using Microsoft.AspNetCore.Http;

// The bundle types are obsolete for callers outside the module, and this is the module.
#pragma warning disable CS0618

namespace BarakoCMS.Portability;

/// <summary>A bundle's records, given their new ids and turned into creates, with what refused them already.</summary>
internal sealed record PreparedEntries(
    List<(int Index, ContentCreateRequest Request)> Pending,
    Dictionary<Guid, Guid> NewIds,
    List<ValidationFailure> Errors);

/// <summary>
/// How a bundle's records become entries, for the import route and for the <c>contents</c> section
/// of a config document alike.
/// </summary>
internal static class BundleEntries
{
    /// <summary>
    /// Gives every record a new id up front, so a reference to another record of the bundle can be
    /// pointed at that record as imported before either exists, and turns each into a create.
    /// </summary>
    public static PreparedEntries Prepare(
        List<ContentRecord> records, List<ContentTypeDefinition> existing, ContentCreateBatch batch, ImportReport report)
    {
        var errors = new List<ValidationFailure>();
        var newIds = new Dictionary<Guid, Guid>();
        for (var i = 0; i < records.Count; i++)
        {
            if (records[i].Id is not { } sourceId || sourceId == Guid.Empty)
            {
                continue;
            }

            if (!newIds.TryAdd(sourceId, Guid.NewGuid()))
            {
                errors.Add(new ValidationFailure($"contents[{i}]", $"id {sourceId} is used by another record of this bundle."));
            }
        }

        var pending = new List<(int Index, ContentCreateRequest Request)>();
        for (var i = 0; i < records.Count; i++)
        {
            if (Pend(i, records[i], existing, batch, report, newIds, errors) is { } request)
            {
                pending.Add((i, request));
            }
        }

        return new PreparedEntries(pending, newIds, errors);
    }

    private static ContentCreateRequest? Pend(
        int index, ContentRecord rec, List<ContentTypeDefinition> existing, ContentCreateBatch batch, ImportReport report,
        Dictionary<Guid, Guid> newIds, List<ValidationFailure> errors)
    {
        if (string.IsNullOrWhiteSpace(rec.ContentType))
        {
            return null;
        }

        report.ContentsCreated++;

        // A record whose type is in neither the store nor the bundle gets no public fields, so its
        // SearchText comes out empty and it is unsearchable while the import still reports success.
        // Counted and named in the report rather than left to be discovered later.
        var schema = existing.FirstOrDefault(t => t.Name.Equals(rec.ContentType, StringComparison.OrdinalIgnoreCase));
        if (schema is null)
        {
            report.ContentsWithoutContentType++;
            if (!report.UnknownContentTypes.Contains(rec.ContentType, StringComparer.OrdinalIgnoreCase))
            {
                report.UnknownContentTypes.Add(rec.ContentType);
            }
        }

        if (rec.Sensitivity is { } level && !Enum.IsDefined(level))
        {
            errors.Add(new ValidationFailure($"contents[{index}]", "sensitivity is not a valid value."));
            return null;
        }

        var data = rec.Data ?? new Dictionary<string, object>();

        // A field the exporter could not read arrives holding its mask, not its value.
        foreach (var masked in rec.MaskedFields ?? [])
        {
            data.Remove(masked);
        }

        RepointReferences(schema, data, newIds);

        if (rec.Id is { } sourceRecordId && newIds.TryGetValue(sourceRecordId, out var expectedId))
        {
            batch.Expect(expectedId, rec.ContentType);
        }

        return new ContentCreateRequest
        {
            ContentType = rec.ContentType,
            Data = data,
            Status = Enum.TryParse<ContentStatus>(rec.Status, ignoreCase: true, out var s) && Enum.IsDefined(s)
                ? s
                : ContentStatus.Published,
            Sensitivity = rec.Sensitivity ?? SensitivityLevel.Public,
            Id = rec.Id is { } id && newIds.TryGetValue(id, out var fresh) ? fresh : null,
        };
    }

    /// <summary>
    /// Checks and stages each create in dependency order, each saved before the next is checked so
    /// a lifecycle hook sees the entries written before it. Returns what was refused.
    /// </summary>
    public static async Task<List<ValidationFailure>> WriteAsync(
        IDocumentSession session, IContentCreator creator, PreparedEntries prepared, Guid userId, HttpContext http,
        ContentCreateBatch batch, CancellationToken ct)
    {
        var refused = new List<ValidationFailure>();
        foreach (var (index, request) in InDependencyOrder(prepared.Pending, prepared.NewIds))
        {
            var errors = await creator.CheckAsync(request, userId, http, batch, ct);
            if (errors.Count > 0)
            {
                refused.AddRange(errors.Select(e => new ValidationFailure($"contents[{index}]", e)));

                // Whatever a hook staged for an entry it then refused is not this batch's.
                session.EjectAllPendingChanges();
                continue;
            }

            try
            {
                await creator.StageAsync(request, userId, batch, ct);
                await session.SaveChangesAsync(ct);
            }
            catch (ContentUniquenessException ex)
            {
                // Another entry, stored or earlier in this bundle, holds this record's values under a
                // uniqueness rule of its type. Named like any other refused record.
                refused.Add(new ValidationFailure($"contents[{index}]", ex.Message));
                session.EjectAllPendingChanges();
            }
        }

        return refused;
    }

    /// <summary>
    /// The entries of <paramref name="contents"/> as bundle records, each as <paramref name="caller"/>
    /// may read it, and how many were left out because the caller may read none of them.
    /// </summary>
    public static async Task<(List<ContentRecord> Records, int Withheld)> ExportAsync(
        IEnumerable<Content> contents, List<ContentTypeDefinition> types, User caller, HttpContext http,
        IPermissionResolver permissions, ISensitivityService sensitivity, CancellationToken ct)
    {
        var records = new List<ContentRecord>();
        var withheld = 0;
        foreach (var c in contents)
        {
            // The same per-entry read rule as the content List endpoint. Whoever may take a bundle
            // is decided by the route's capability, not which rows they may read.
            if (!await permissions.CanPerformActionAsync(caller, c.ContentType, "read", c, ct))
            {
                withheld++;
                continue;
            }

            var data = new Dictionary<string, object>(c.Data);
            var hidden = await sensitivity.ApplyAsync(c.ContentType, c.Sensitivity, data, http, ct);

            // The read endpoints show an entry the caller may read nothing of as empty or HIDDEN. An
            // import would turn that into a real, empty entry, so it is left out and counted instead.
            if (hidden || (data.Count == 0 && c.Data.Count > 0))
            {
                withheld++;
                continue;
            }

            // A token is generated where an entry is created, and an import creates entries, so a
            // token in a bundle is discarded on the way in. It is left out here for every caller,
            // those who may read it included: a bundle is a file, and a file gets passed on.
            var definition = types.FirstOrDefault(t => string.Equals(t.Name, c.ContentType, StringComparison.OrdinalIgnoreCase));
            foreach (var field in (definition?.Fields ?? [])
                         .Where(f => f is not null && barakoCMS.Core.Validation.FieldTypeRegistry.IsServerGenerated(f)))
            {
                foreach (var key in data.Keys.Where(k => string.Equals(k, field.Name, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    data.Remove(key);
                }
            }

            records.Add(new ContentRecord
            {
                Id = c.Id,
                ContentType = c.ContentType,
                Data = data,
                Status = c.Status.ToString(),
                Sensitivity = c.Sensitivity,
                MaskedFields = data.Where(kv => !ReferenceEquals(kv.Value, c.Data[kv.Key])).Select(kv => kv.Key).ToList(),
            });
        }

        return (records, withheld);
    }

    /// <summary>
    /// Points each reference field holding the source id of another record in this bundle at the id
    /// that record is imported under, each id of a list included. A reference to anything outside
    /// the bundle is left alone, and validation refuses it unless it exists here.
    /// </summary>
    private static void RepointReferences(
        ContentTypeDefinition? schema, Dictionary<string, object> data, Dictionary<Guid, Guid> newIds)
    {
        if (schema is null || newIds.Count == 0)
        {
            return;
        }

        foreach (var field in schema.Fields.Where(f => string.Equals(f.Type, "reference", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var key in data.Keys.Where(k => k.Equals(field.Name, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                if (barakoCMS.Core.Validation.FieldTypeRegistry.TryReadChoice(data[key], out var listed, out var isList) && isList)
                {
                    data[key] = listed
                        .Select(id => Guid.TryParse(id, out var source) && newIds.TryGetValue(source, out var copy) ? copy.ToString() : id)
                        .ToList<object>();
                    continue;
                }

                var raw = data[key] is System.Text.Json.JsonElement je ? je.ToString() : data[key]?.ToString();
                if (Guid.TryParse(raw, out var target) && newIds.TryGetValue(target, out var imported))
                {
                    data[key] = imported.ToString();
                }
            }
        }
    }

    /// <summary>
    /// The records, each after the records of this bundle it references, otherwise in bundle order.
    /// </summary>
    /// <remarks>
    /// A reference is checked against what exists, so a child has to be written after its parent,
    /// and an export does not promise that order. A cycle cannot be ordered; its records keep their
    /// bundle order and the first of them is refused for pointing at an entry not yet written.
    /// </remarks>
    private static IEnumerable<(int Index, ContentCreateRequest Request)> InDependencyOrder(
        List<(int Index, ContentCreateRequest Request)> pending, Dictionary<Guid, Guid> newIds)
    {
        var byNewId = pending.Where(p => p.Request.Id is not null).ToDictionary(p => p.Request.Id!.Value, p => p.Index);
        var imported = newIds.Values.ToHashSet();

        var dependsOn = pending.ToDictionary(p => p.Index, p => p.Request.Data.Values
            .Select(v => v is System.Text.Json.JsonElement je ? je.ToString() : v?.ToString())
            .Select(v => Guid.TryParse(v, out var g) && imported.Contains(g) && byNewId.TryGetValue(g, out var at) && at != p.Index ? at : -1)
            .Where(at => at >= 0)
            .ToHashSet());

        var written = new HashSet<int>();
        var remaining = pending.ToList();
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(p => dependsOn[p.Index].All(written.Contains));
            if (next.Request is null)
            {
                next = remaining[0];
            }

            remaining.Remove(next);
            written.Add(next.Index);
            yield return next;
        }
    }
}
