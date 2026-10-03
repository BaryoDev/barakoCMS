using Marten;
using Marten.Linq.MatchesSql;
using barakoCMS.Core.Validation;
using barakoCMS.Models;
using System.Text.Json;

namespace barakoCMS.Infrastructure.Services;

public interface IContentValidatorService
{
    /// <summary>Checks a data bag against its content type's schema.</summary>
    /// <param name="contentType">The type name as the caller spelled it.</param>
    /// <param name="data">The field values being written.</param>
    /// <param name="existing">
    /// The entry being changed, or null when one is being created. Only the singleton cap reads it,
    /// and null is the answer that enforces the cap, so a create path that passes nothing still gets
    /// the check.
    /// </param>
    /// <remarks>
    /// Anything implementing or decorating this interface wants to override this member rather than
    /// only the obsolete two-argument one. Inheriting that default passes null, and null is read as a
    /// create: the singleton cap then refuses an update of the one entry as a second entry, and the
    /// slug check refuses an entry its own stored slug on the next edit.
    /// </remarks>
    Task<(bool IsValid, List<string> Errors)> ValidateAsync(
        string contentType,
        Dictionary<string, object> data,
        Models.Content? existing = null);

    /// <summary>Checks a data bag against its content type's schema, as a create.</summary>
    /// <remarks>
    /// Kept because BarakoCMS.Import 4.0.0 is published and its compiled call site names this
    /// member: dropping it means a host that upgrades the core package while keeping that module
    /// gets a MissingMethodException on POST /api/import/content, with nothing at compile time to
    /// warn them. A default implementation rather than a second method on the class, so an
    /// implementor written against the three-argument overload does not have to write this one.
    /// </remarks>
    [Obsolete("Use the overload taking existing, so an update of a singleton type's only entry is "
        + "not read as a second entry. Removal planned for barakoCMS 5.0.")]
    Task<(bool IsValid, List<string> Errors)> ValidateAsync(
        string contentType,
        Dictionary<string, object> data)
        => ValidateAsync(contentType, data, existing: null);

    /// <summary>
    /// Checks a data bag against a schema the caller supplies rather than the stored one, leaving
    /// out the singleton cap.
    /// </summary>
    /// <remarks>
    /// For a write that changes the type and its entries in one unit, as an import does: the type
    /// the entries must match is not stored yet. The singleton cap is left to the caller, because
    /// whether a batch may hold more than one entry of a singleton type is the batch's rule.
    ///
    /// The default throws rather than falling back to the stored schema. Falling back would check a
    /// type the bundle is about to replace, or no type at all, and pass what should be refused.
    /// </remarks>
    Task<(bool IsValid, List<string> Errors)> ValidateFieldsAsync(
        ContentTypeDefinition schema,
        string contentType,
        Dictionary<string, object> data,
        Models.Content? existing)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement {nameof(ValidateFieldsAsync)}.");

    /// <summary>Checks a data bag against its content type's schema, for the user the write is made for.</summary>
    /// <param name="contentType">The type name as the caller spelled it.</param>
    /// <param name="data">The field values being written.</param>
    /// <param name="existing">The stored entry being changed, or null when one is being created.</param>
    /// <param name="caller">
    /// The user the write is made for: the principal of their request, or one naming them. A
    /// <c>file</c> field takes a file this user may download. Null is a write no user makes, and a
    /// file field then takes a public file only, which is also what the overloads without a caller
    /// check.
    /// </param>
    /// <remarks>
    /// The default ignores the caller, which is what an implementor written before this member does.
    /// </remarks>
    Task<(bool IsValid, List<string> Errors)> ValidateAsync(
        string contentType,
        Dictionary<string, object> data,
        Models.Content? existing,
        System.Security.Claims.ClaimsPrincipal? caller)
        => ValidateAsync(contentType, data, existing);

    /// <summary>
    /// Checks a data bag against a schema the caller supplies, for the user the write is made for,
    /// as the overload of <c>ValidateAsync</c> taking a caller does.
    /// </summary>
    /// <remarks>The default ignores the caller.</remarks>
    Task<(bool IsValid, List<string> Errors)> ValidateFieldsAsync(
        ContentTypeDefinition schema,
        string contentType,
        Dictionary<string, object> data,
        Models.Content? existing,
        System.Security.Claims.ClaimsPrincipal? caller)
        => ValidateFieldsAsync(schema, contentType, data, existing);
}

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
        var errors = new List<string>();

        // 3. Validate Fields
        foreach (var field in schema.Fields)
        {
            // The server owns a token's value. Whatever is in the data here is not what is stored:
            // the writer puts the stored token back, or generates one, after this has run.
            if (TokenFields.IsToken(field.Type))
                continue;

            var keyDetails = data.FirstOrDefault(k => k.Key.Equals(field.Name, StringComparison.OrdinalIgnoreCase));

            // Only the first of two keys differing in case is read below, and delivery resolves
            // both, so a file field sent twice is refused before anything else, a null first one
            // included.
            if (FileFields.IsFileField(field)
                && data.Keys.Count(k => k.Equals(field.Name, StringComparison.OrdinalIgnoreCase)) > 1)
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

            // Check Type - validate the value against the field type via the shared
            // registry (same source of truth the content-type validator uses).
            if (keyDetails.Key != null && keyDetails.Value != null)
            {
                var value = keyDetails.Value;
                var expectedType = field.Type.ToLower();

                if (ReferenceFields.IsMultiple(field))
                {
                    if (await ReferenceFields.ValueErrorAsync(session, field, value) is { } referencesError)
                        errors.Add(referencesError);
                }
                else if (expectedType == FileFields.TypeName)
                {
                    // Ahead of the shape check, so text that is not an id is answered in the same
                    // words as an id the caller may not use.
                    var error = await ValidateFileAsync(field, value, existing, caller);
                    if (error is not null)
                        errors.Add(error);
                }
                else if (!FieldTypeRegistry.IsValidValue(expectedType, value))
                {
                    var actualType = GetActualTypeName(value);
                    errors.Add($"Field '{field.DisplayName}' expects type '{expectedType}' but received '{actualType}'");
                }
                else if (expectedType == "reference")
                {
                    // The registry checked the shape. Whether the target exists, and is the type
                    // this field declares, needs the database, which is why it is here and not
                    // there. Checked on write rather than on read: a reference that pointed at
                    // nothing would otherwise be stored happily and fail for whoever renders it.
                    var error = await ValidateReferenceAsync(field, value);
                    if (error is not null)
                        errors.Add(error);
                }
                else if (expectedType == "choice")
                {
                    var error = ValidateChoice(field, value);
                    if (error is not null)
                        errors.Add(error);
                }
                else if (MoneyFields.ValueError(field, value) is { } moneyError)
                {
                    errors.Add(moneyError);
                }
                else
                {
                    errors.AddRange(FieldRules.ValueErrors(field, value));
                }
            }
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
        var slugField = barakoCMS.Features.Public.PublicDelivery.SlugField(schema);
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
    /// refuses. The error names every accepted value, because the fix is to pick one of them.
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

        var unknown = values
            .Where(v => !accepted.Contains(v, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (unknown.Count > 0)
            return $"Field '{field.DisplayName}' accepts {acceptedText}, and "
                 + string.Join(", ", unknown.Select(u => $"'{u}'"))
                 + (unknown.Count == 1 ? " is" : " are") + " not one of them.";

        var repeated = values
            .GroupBy(v => v, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"'{g.Key}'")
            .ToList();

        if (repeated.Count > 0)
            return $"Field '{field.DisplayName}' lists {string.Join(", ", repeated)} more than once.";

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
