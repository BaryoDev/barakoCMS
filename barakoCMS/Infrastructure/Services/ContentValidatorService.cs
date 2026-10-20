using Marten;
using Marten.Linq.MatchesSql;
using barakoCMS.Core.Validation;
using barakoCMS.Models;
using System.Text.Json;

namespace barakoCMS.Infrastructure.Services;

public class ContentValidatorService(
    IQuerySession session,
    barakoCMS.Core.Interfaces.IFileStore? files) : IContentValidatorService
{
    /// <summary>A validator with no file store, which refuses a new value in a <c>file</c> field.</summary>
    public ContentValidatorService(IQuerySession session)
        : this(session, null)
    {
    }

    public Task<(bool IsValid, List<string> Errors)> ValidateAsync(
        string contentType,
        Dictionary<string, object> data,
        Models.Content? existing = null)
        => ValidateAsync(contentType, data, existing, caller: null);

    /// <inheritdoc />
    public async Task<(bool IsValid, List<string> Errors)> ValidateAsync(
        string contentType,
        Dictionary<string, object> data,
        Models.Content? existing,
        System.Security.Claims.ClaimsPrincipal? caller)
    {
        var errors = new List<string>();

        // A field change rewrites entries in batches; a write between two would undo its work.
        if (await barakoCMS.Infrastructure.Jobs.FieldChanges.RunningAsync(session, contentType, CancellationToken.None) is { } change)
        {
            errors.Add($"'{change.ContentType}' has a field change running ({change.Id}). Writes to it resume when the change completes.");
            return (false, errors);
        }

        // 1. Load Schema
        var schema = await session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(x => x.Name == contentType);

        // 2. The singleton cap. Creating only: an update is not a second entry, and refusing it
        // would make the flag unusable, since the one entry a singleton type is for could never be
        // edited.
        //
        // The definition is resolved again here, case-insensitively, instead of reusing the exact
        // match above. That lookup stays exact on purpose: a mis-cased name finds no schema and the
        // entry is accepted unvalidated, and loosening it would start refusing requests this API
        // takes today, which is an HTTP-surface break under CLAUDE.md section 6. The cap still has
        // to see the type, because an entry created as SETTINGS is a second entry of settings, and a
        // cap a caller can walk past by holding down shift is not a cap. Case-insensitive for the
        // reason every other name comparison here is: names have only been normalised since 4.0, so
        // a type or an entry written by a 3.x import carries whatever the caller typed.
        if (existing is null)
        {
            var lowered = contentType.ToLower();

            var definition = schema ?? await session.Query<ContentTypeDefinition>()
                .FirstOrDefaultAsync(x => x.Name.ToLower() == lowered);

            if (definition?.IsSingleton == true)
            {
                var typeName = definition.Name.ToLower();

                // Every status counts, drafts and archived entries included. The cap exists so the
                // type holds one row, and a reader that takes the first item of the list cannot tell
                // an archived row from a live one. Freeing the slot means erasing the entry, which
                // needs SuperAdmin and the EraseContent capability.
                var taken = await session.Query<Models.Content>()
                    .AnyAsync(c => c.ContentType.ToLower() == typeName);

                if (taken)
                {
                    errors.Add(
                        $"'{definition.DisplayName}' holds a single entry and already has one. "
                      + "Edit that entry rather than creating another.");

                    // No point reporting field errors on a request that cannot be created either way.
                    return (false, errors);
                }
            }
        }

        if (schema == null)
        {
            // No content type definition, so there is no schema to check against and the entry is
            // accepted as-is. Validation is opt-in: defining a type is what turns it on.
            return (true, errors);
        }

        return await ValidateFieldsAsync(schema, contentType, data, existing, caller);
    }

    /// <inheritdoc />
    public Task<(bool IsValid, List<string> Errors)> ValidateFieldsAsync(
        ContentTypeDefinition schema,
        string contentType,
        Dictionary<string, object> data,
        Models.Content? existing)
        => ValidateFieldsAsync(schema, contentType, data, existing, caller: null);

    /// <inheritdoc />
    public async Task<(bool IsValid, List<string> Errors)> ValidateFieldsAsync(
        ContentTypeDefinition schema,
        string contentType,
        Dictionary<string, object> data,
        Models.Content? existing,
        System.Security.Claims.ClaimsPrincipal? caller)
    {
        var errors = UndeclaredFieldErrors(schema, contentType, data, existing);

        // 3. Validate Fields
        foreach (var field in schema.Fields)
        {
            // The server owns a token's value. Whatever is in the data here is not what is stored:
            // the writer puts the stored token back, or generates one, after this has run.
            if (TokenFields.IsToken(field.Type))
                continue;

            var keyDetails = data.FirstOrDefault(k => k.Key.Equals(field.Name, StringComparison.OrdinalIgnoreCase));

            // The server computes the value inside the write. A request may carry the stored value
            // back, as an edit form does, and nothing else.
            if (ComputedFields.IsComputed(field))
            {
                var storedKey = existing?.Data.Keys.FirstOrDefault(k => k.Equals(field.Name, StringComparison.OrdinalIgnoreCase));
                var stored = storedKey is null ? null : existing!.Data[storedKey];
                if (keyDetails.Key != null && !ComputedFields.SameAsStored(keyDetails.Value, stored))
                    errors.Add(ComputedFields.RefusedMessage(field));

                continue;
            }

            // Only the first of two keys differing in case is checked below, and both are stored,
            // so a reader that takes the other one would get a value nothing checked. A field sent
            // twice is refused before anything else, a null first one included.
            if (data.Keys.Count(k => k.Equals(field.Name, StringComparison.OrdinalIgnoreCase)) > 1)
            {
                errors.Add($"Field '{field.DisplayName}' ({field.Name}) was sent more than once, ignoring case.");
                continue;
            }

            // Check Required
            var requiredByRule = !field.IsRequired && FieldRules.IsRequiredBy(field, data);
            if (field.IsRequired || requiredByRule)
            {
                if (keyDetails.Key == null || IsBlank(field, keyDetails.Value))
                {
                    errors.Add(requiredByRule
                        ? $"Field '{field.DisplayName}' ({field.Name}) is required for this entry (rule 'requiredWhen')."
                        : $"Field '{field.DisplayName}' ({field.Name}) is required.");
                    continue;
                }
            }

            if (keyDetails.Key != null && keyDetails.Value != null)
                errors.AddRange(await ValueErrorsAsync(field, keyDetails.Value, existing, caller));
        }

        // Only when everything else passed. This one costs a query the slug route's own comment
        // explains cannot use an index, and a request already answering 400 does not need a second
        // reason to; on a bulk import it is one such query per row.
        //
        // The entry's own stored slug is not a collision with itself, so the entry being changed is
        // excluded. `existing` is that entry, which is why no second parameter carrying its id is
        // needed: every caller that has the id has the entry.
        if (errors.Count == 0)
        {
            var slugError = await ValidateSlugUniquenessAsync(schema, contentType, data, existing?.Id);
            if (slugError is not null)
                errors.Add(slugError);
        }

        return (errors.Count == 0, errors);
    }

    /// <summary>What is wrong with the value a write sends for one field, by the field's type.</summary>
    /// <remarks>
    /// The type's own check first, through the shared registry (the same source of truth the
    /// content-type validator uses), then whatever the definition adds to it.
    /// </remarks>
    private async Task<IEnumerable<string>> ValueErrorsAsync(
        FieldDefinition field,
        object value,
        Models.Content? existing,
        System.Security.Claims.ClaimsPrincipal? caller)
    {
        var expectedType = field.Type.ToLower();

        if (InlineImageFields.Is(expectedType))
        {
            return InlineImageFields.IsUnchanged(field, value, existing) ? [] : One(InlineImageFields.ValueError(field, value));
        }

        if (ReferenceFields.IsMultiple(field))
        {
            return One(await ReferenceFields.ValueErrorAsync(session, field, value));
        }

        // Ahead of the shape check, so text that is not an id is answered in the same words as an
        // id the caller may not use.
        if (expectedType == FileFields.TypeName)
        {
            return One(await ValidateFileAsync(field, value, existing, caller));
        }

        // An address stored before the email check was tightened is still the entry's own, and an
        // edit of another field keeps it as it is.
        if (expectedType == "email" && StoredValues.IsUnchanged(field.Name, value, existing))
        {
            return [];
        }

        if (!FieldTypeRegistry.IsValidValue(expectedType, value))
        {
            return [$"Field '{field.DisplayName}' expects type '{expectedType}' but received '{GetActualTypeName(value)}'"];
        }

        // The registry checked the shape. Whether the target exists, and is the type this field
        // declares, needs the database, which is why it is here and not there. Checked on write
        // rather than on read: a reference that pointed at nothing would otherwise be stored happily
        // and fail for whoever renders it.
        if (expectedType == "reference")
        {
            return One(await ValidateReferenceAsync(field, value));
        }

        // What the entry already holds is not checked again. The options may have changed since it
        // was stored, and a field the caller may not see is put back to its stored value before this
        // runs, so refusing it would block every edit of the rest of the entry over a value the
        // caller did not send.
        if (expectedType == "choice")
        {
            return StoredValues.IsUnchanged(field.Name, value, existing) ? [] : One(ValidateChoice(field, value));
        }

        if (BlockFields.Is(expectedType))
        {
            return await BlockFields.ValueErrorsAsync(session, field, value, existing);
        }

        if (expectedType == "json" && JsonFieldSchemas.Declares(field))
        {
            return JsonFieldSchemas.ValueErrors(field, value, existing);
        }

        var typed = ArrayItems.ValueError(field, value, existing) ?? MoneyFields.ValueError(field, value);
        return typed is not null ? [typed] : FieldRules.ValueErrors(field, value);
    }

    private static IEnumerable<string> One(string? error) => error is null ? [] : [error];

    /// <summary>
    /// Refuses a key the type does not declare as a field, so nothing is stored that no field
    /// describes, no validator checked and no sensitivity setting covers.
    /// </summary>
    /// <remarks>
    /// Names are matched ignoring case, as every other reader of a field does. A key the entry
    /// already holds with the same value is let through: an entry stored before this check, or before
    /// a field was removed from its type, can still be edited without first being cleaned.
    /// </remarks>
    private static List<string> UndeclaredFieldErrors(
        ContentTypeDefinition schema,
        string contentType,
        Dictionary<string, object> data,
        Models.Content? existing)
    {
        var undeclared = data
            .Where(kv => !schema.Fields.Any(f => string.Equals(f.Name, kv.Key, StringComparison.OrdinalIgnoreCase))
                && !StoredValues.IsUnchanged(kv.Key, kv.Value, existing))
            .Select(kv => kv.Key)
            .ToList();

        if (undeclared.Count == 0)
        {
            return [];
        }

        var named = string.Join(", ", undeclared.Take(5).Select(k => $"'{k}'"))
            + (undeclared.Count > 5 ? $" and {undeclared.Count - 5} more" : string.Empty);
        return [$"'{contentType}' does not declare {named}. Add each to the type as a field, or leave it out."];
    }

    /// <summary>
    /// Checks that no other entry of this type already holds this entry's slug.
    /// </summary>
    /// <remarks>
    /// A slug is how a URL names one entry, and nothing enforced that it named only one: the slug
    /// route resolves with <c>FirstOrDefaultAsync</c>, so two entries of a type sharing a slug served
    /// whichever row Postgres returned first, and which one that is can change between requests.
    ///
    /// Every status, not only Published. Leaving drafts out would allow a draft that cannot be
    /// published, and the moment it was discovered is the moment somebody published it: the scheduler
    /// does that on a timer with no request to answer and nobody to refuse. Checking on the way in
    /// means a status change can never create a collision, so <c>ChangeStatus</c> and the sweeper need
    /// no rule of their own.
    ///
    /// Which field is the slug, and how a slug is matched, both come from the delivery code that
    /// resolves it. A second answer to either question here would be a uniqueness rule that does not
    /// prevent the ambiguity it exists to prevent: a case-sensitive check, for instance, would accept
    /// two entries the case-insensitive route cannot tell apart.
    /// </remarks>
    private async Task<string?> ValidateSlugUniquenessAsync(
        ContentTypeDefinition schema,
        string contentType,
        Dictionary<string, object> data,
        Guid? entryId)
    {
        var slugField = barakoCMS.Features.Public.PublicDelivery.SlugFieldForAuthoring(schema);
        if (slugField is null)
            return null;

        var submitted = data.FirstOrDefault(kv => kv.Key.Equals(slugField, StringComparison.OrdinalIgnoreCase));
        if (submitted.Key is null)
            return null;

        var slug = submitted.Value is JsonElement je ? je.ToString() : submitted.Value?.ToString();
        if (string.IsNullOrWhiteSpace(slug))
            return null;

        var (sql, parameters) = barakoCMS.Features.Public.DeliveryQuery.FieldEqualsIgnoreCaseSql(slugField, slug);

        var holders = session.Query<Models.Content>()
            .Where(c => c.ContentType == contentType && c.MatchesSql(sql, parameters));

        if (entryId is { } id)
            holders = holders.Where(c => c.Id != id);

        if (!await holders.AnyAsync())
            return null;

        var display = schema.Fields
            .FirstOrDefault(f => string.Equals(f.Name, slugField, StringComparison.OrdinalIgnoreCase))
            ?.DisplayName ?? slugField;

        // Which entry holds it is deliberately not named. A caller who may create content here is not
        // necessarily allowed to read the entry in the way, and a draft's existence is the draft's.
        return $"Field '{display}' must be unique, and '{slug}' is already used by another "
             + $"'{contentType}' entry.";
    }

    /// <summary>
    /// Checks that a reference points at something, and at the right kind of something.
    /// </summary>
    /// <remarks>
    /// Pointing at a real entry of the wrong type is the more interesting failure of the two. It
    /// looks correct in the data bag, passes any shape check, and produces a resolved value the
    /// consumer did not ask for. Naming the target type in the error matters for the same reason:
    /// "not found" and "wrong type" are different mistakes and the caller can only fix the one they
    /// are told about.
    /// </remarks>
    private async Task<string?> ValidateReferenceAsync(FieldDefinition field, object value)
    {
        var raw = value is JsonElement je ? je.ToString() : value.ToString();
        if (!Guid.TryParse(raw, out var targetId))
            return $"Field '{field.DisplayName}' expects a reference id.";

        var target = await session.LoadAsync<Models.Content>(targetId);

        if (target is null)
            return $"Field '{field.DisplayName}' references {targetId}, which does not exist.";

        if (!string.Equals(target.ContentType, field.ReferenceType, StringComparison.OrdinalIgnoreCase))
            return $"Field '{field.DisplayName}' references a '{target.ContentType}' "
                 + $"but is declared to point at '{field.ReferenceType}'.";

        return null;
    }

    /// <summary>
    /// Checks that a file field names a file the caller of this request may use.
    /// </summary>
    /// <remarks>
    /// Asked of the file store as the user the write is made for, so the answer is the one the
    /// download routes give: any public file, or a private one that is the caller's own or that the
    /// caller administers. A write with no caller, such as a job or a system actor, takes a public
    /// file only.
    ///
    /// The value the entry already holds in this field is not asked about again. It was checked for
    /// whoever attached it, and asking for it as this caller would refuse every later edit of the
    /// entry by anyone else, and every edit after the file was deleted.
    /// </remarks>
    private async Task<string?> ValidateFileAsync(
        FieldDefinition field,
        object value,
        Models.Content? existing,
        System.Security.Claims.ClaimsPrincipal? caller)
    {
        if (existing is not null && FileFields.Holds(existing.Data, field.Name, value))
            return null;

        if (files is null or NoFileStore)
            return FileFields.NoStore(field);

        if (!FileFields.TryReadId(value, out var id))
            return FileFields.Refused(field);

        var file = caller is null
            ? await files.FindPublicAsync(id)
            : await files.FindAsync(id, caller);

        return file is null ? FileFields.Refused(field) : null;
    }

    /// <summary>Whether a required field holding this value counts as left out.</summary>
    internal static bool IsBlank(FieldDefinition field, object? value) =>
        value is null || string.IsNullOrWhiteSpace(value.ToString()) || IsEmptyList(field, value);

    /// <summary>A required multiple choice or reference holding an empty list has nothing in it.</summary>
    private static bool IsEmptyList(FieldDefinition field, object value) =>
        (string.Equals(field.Type, "choice", StringComparison.OrdinalIgnoreCase) || ReferenceFields.IsMultiple(field))
        && FieldTypeRegistry.TryReadChoice(value, out var values, out var isList)
        && isList
        && values.Count == 0;

    /// <summary>
    /// Checks that a choice value is one of the field's options, or a list of them for a field that
    /// takes several.
    /// </summary>
    /// <remarks>
    /// Matched exactly, case included. The value is the stable key a filter, a workflow condition and
    /// a renderer's colour map all compare against, so 'fun' beside 'FUN' is exactly the drift this
    /// refuses. The error names every accepted value, because the fix is to pick one of them, and
    /// never the value received, so a message cannot carry a stored value back to a caller.
    /// </remarks>
    private static string? ValidateChoice(FieldDefinition field, object value)
    {
        FieldTypeRegistry.TryReadChoice(value, out var values, out var isList);

        var accepted = (field.Options ?? new List<FieldOption>())
            .Where(o => o is not null)
            .Select(o => o.Value)
            .ToList();
        var acceptedText = accepted.Count == 0 ? "no values" : string.Join(", ", accepted);

        if (field.Multiple && !isList)
            return $"Field '{field.DisplayName}' holds a list of options, so send a list, even of one.";

        if (!field.Multiple && isList)
            return $"Field '{field.DisplayName}' holds one option and received a list.";

        if (values.Any(v => !accepted.Contains(v, StringComparer.Ordinal)))
            return $"Field '{field.DisplayName}' ({field.Name}) accepts {acceptedText}, and received a value "
                 + "that is not one of them.";

        if (values.Distinct(StringComparer.Ordinal).Count() < values.Count)
            return $"Field '{field.DisplayName}' ({field.Name}) lists an option more than once.";

        return null;
    }

    private string GetActualTypeName(object value)
    {
        if (value is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.String => "string",
                JsonValueKind.Number => "number",
                JsonValueKind.True or JsonValueKind.False => "boolean",
                JsonValueKind.Array => "array",
                JsonValueKind.Object => "object",
                JsonValueKind.Null => "null",
                _ => "unknown"
            };
        }

        return value.GetType().Name.ToLower();
    }
}
