using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Writing a content type definition over the stored one of the same name, or as a new type: what
/// is carried over, what is refused, and what is stored.
/// </summary>
/// <remarks>
/// One copy for the two writers that take a whole definition, the Portability import and the config
/// engine, so what one refuses the other refuses too. Public because the import is a module.
/// </remarks>
public static class ContentTypeUpsert
{
    /// <summary>How a refusal names the writer and what it read the definition from.</summary>
    /// <param name="Source">What the definition came from, for example "the bundle".</param>
    /// <param name="Writer">Who writes it, for example "an import", at the start of a sentence.</param>
    public sealed record Wording(string Source, string Writer)
    {
        public static readonly Wording Import = new("the bundle", "An import");
        public static readonly Wording Config = new("the document", "An apply");

        public string Possessive => Source + "'s";
    }

    /// <summary>
    /// The stored type a definition updates, compared under the name normalisation create applies,
    /// so "Blog Post" updates a stored "blog-post" rather than colliding with it.
    /// </summary>
    public static ContentTypeDefinition? StoredMatch(IEnumerable<ContentTypeDefinition> existing, string name)
    {
        var normalized = barakoCMS.Core.ContentTypeName.Normalize(name);
        return existing.FirstOrDefault(t => barakoCMS.Core.ContentTypeName.Normalize(t.Name) == normalized);
    }

    /// <summary>
    /// Brings a definition to the shape it is checked and stored in: no null fields, the type
    /// defaults applied, roles as ids on both sides, and the stored presentation kept where the
    /// definition carries none.
    /// </summary>
    public static async Task PrepareAsync(
        IQuerySession session, ContentTypeDefinition type, ContentTypeDefinition? stored, CancellationToken ct)
    {
        type.Fields = (type.Fields ?? []).Where(f => f is not null).ToList();
        foreach (var field in type.Fields)
        {
            field.VisibleToRoles ??= [];
            field.ValidationRules ??= new Dictionary<string, object>();
        }

        // A token field that states no sensitivity is Hidden, as on create. Before the definition
        // is compared with the stored type, so leaving it out is not read as lowering a stored field.
        barakoCMS.Core.Validation.FieldTypeRegistry.ApplyTypeDefaults(type.Fields);

        // A definition names roles and a stored one holds their ids. Both sides are brought to ids
        // before they are compared, so naming the roles a stored field lists is not a change.
        await barakoCMS.Core.RoleReferences.ToIdsAsync(session, type.Fields, ct);
        if (stored is not null)
        {
            await barakoCMS.Core.RoleReferences.ToIdsAsync(session, stored.Fields ?? [], ct);
            KeepStoredPresentation(type, stored);
        }
    }

    /// <summary>
    /// The refusal when <paramref name="type"/> holds more fields than the cap allows and more than
    /// the stored type already holds, or null. A type stored over the cap may be written at its size.
    /// </summary>
    public static string? FieldCapError(ContentTypeDefinition type, ContentTypeDefinition? stored, int maxFields)
    {
        var incoming = type.Fields?.Count ?? 0;
        var held = stored?.Fields?.Count ?? 0;

        return incoming > maxFields && incoming > held ? ContentTypeFieldLimit.TooMany(maxFields, incoming) : null;
    }

    /// <summary>
    /// What the content-type endpoints would refuse about writing <paramref name="type"/> over
    /// <paramref name="stored"/>, or as a new type when <paramref name="stored"/> is null.
    /// </summary>
    public static async Task<List<string>> ErrorsAsync(
        IContentTypeValidatorService validator, IContentSourcingPolicy sourcing, IQuerySession session,
        ContentTypeDefinition type, ContentTypeDefinition? stored, int maxFields, Wording wording, CancellationToken ct)
    {
        var errors = DefinitionErrors(validator, type, stored, maxFields);

        var name = stored?.Name ?? barakoCMS.Core.ContentTypeName.Normalize(type.Name);
        var nonPublic = type.Fields.Where(f => f.Sensitivity != SensitivityLevel.Public).Select(f => f.Name).ToList();
        if (nonPublic.Count > 0 && await sourcing.IsEventSourcedAsync(name, ct))
        {
            errors.Add($"'{name}' is event sourced, so its fields have to stay Public, and "
                       + $"{string.Join(", ", nonPublic)} {(nonPublic.Count == 1 ? "is" : "are")} not.");
        }

        // A token field the stored type does not have yet must not take over values entries
        // already hold under its name, the add field rule.
        foreach (var token in type.Fields.Where(f => barakoCMS.Core.Validation.FieldTypeRegistry.IsServerGenerated(f)
                     && !(stored?.Fields ?? []).Any(s => s is not null
                         && s.Name.Equals(f.Name, StringComparison.OrdinalIgnoreCase)
                         && barakoCMS.Core.Validation.FieldTypeRegistry.IsServerGenerated(s))))
        {
            var holding = await TokenFieldEntries.HoldingAsync(session, name, token.Name, ct);
            if (holding > 0)
            {
                errors.Add(TokenFieldEntries.Refusal(name, token.Name, holding));
            }
        }

        if (stored is null)
        {
            return errors;
        }

        errors.AddRange(SensitivityChanges(type, stored, wording));
        errors.AddRange(DroppedTransitionFields(type, stored, wording));
        errors.AddRange(DroppedNumberingAndLookupFields(type, stored, wording));
        errors.AddRange(UniquenessChanges(type, stored, wording));
        errors.AddRange(CurrencyAndTokenChanges(type, stored, wording));
        errors.AddRange(EntryRules(type, stored, await EntryCountAsync(session, stored, ct), wording));

        return errors;
    }

    /// <summary>The stored type with <paramref name="type"/> written over it, the way an upsert stores it.</summary>
    public static void MergeInto(ContentTypeDefinition stored, ContentTypeDefinition type)
    {
        stored.DisplayName = type.DisplayName;
        stored.Description = type.Description;
        stored.Fields = type.Fields;
        // A definition without a lifecycle keeps the stored one. ErrorsAsync refuses one that
        // would change it.
        stored.Lifecycle ??= type.Lifecycle;
        stored.IsPubliclyDeliverable = type.IsPubliclyDeliverable;
        stored.IsSingleton = type.IsSingleton;
        // Kept when the definition carries none, for the reason KeepStoredPresentation gives.
        stored.RouteTemplate = type.RouteTemplate ?? stored.RouteTemplate;
        stored.StructuredDataType = type.StructuredDataType ?? stored.StructuredDataType;
        stored.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>A new stored type from <paramref name="type"/>, under the normalised name.</summary>
    public static ContentTypeDefinition Create(ContentTypeDefinition type) => new()
    {
        Id = Guid.NewGuid(),
        // Normalized, like the create endpoint. Storing the given spelling let "Article" sit beside
        // an existing "article": distinct to the unique index, the same to every reader.
        Name = barakoCMS.Core.ContentTypeName.Normalize(type.Name),
        DisplayName = type.DisplayName,
        Description = type.Description,
        Fields = type.Fields,
        Lifecycle = type.Lifecycle,
        IsPubliclyDeliverable = type.IsPubliclyDeliverable,
        IsSingleton = type.IsSingleton,
        RouteTemplate = type.RouteTemplate,
        StructuredDataType = type.StructuredDataType,
        Uniqueness = type.Uniqueness is { Count: > 0 } ? type.Uniqueness : null,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>How many entries a stored type holds, its name compared ignoring case.</summary>
    public static async Task<int> EntryCountAsync(IQuerySession session, ContentTypeDefinition stored, CancellationToken ct)
    {
        var lowered = stored.Name.ToLower();
        return await session.Query<Content>().CountAsync(c => c.ContentType.ToLower() == lowered, ct);
    }

    /// <summary>
    /// Gives each field the editor, section and role its stored counterpart holds, where the
    /// definition carries none.
    /// </summary>
    /// <remarks>
    /// A bundle exported before these members existed cannot carry them, and once read a member
    /// left out and a member set to null are the same. Replacing would clear every hint on the
    /// stored type with the write still reporting success. So null keeps and a value replaces,
    /// which means a definition cannot clear one: the presentation endpoint does that.
    ///
    /// Only on a field whose type stays as it is, since each value is tied to field types. A stored
    /// role is not kept when the definition gives that role to another field, so a role can move.
    /// </remarks>
    private static void KeepStoredPresentation(ContentTypeDefinition type, ContentTypeDefinition stored)
    {
        foreach (var field in type.Fields)
        {
            var current = (stored.Fields ?? []).FirstOrDefault(
                f => f is not null && string.Equals(f.Name, field.Name, StringComparison.OrdinalIgnoreCase));

            if (current is null || !string.Equals(current.Type, field.Type, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            field.Editor ??= current.Editor;
            field.Section ??= current.Section;

            if (field.Role is null
                && current.Role is not null
                && !type.Fields.Any(f => string.Equals(f.Role, current.Role, StringComparison.Ordinal)))
            {
                field.Role = current.Role;
            }
        }
    }

    private static List<string> DefinitionErrors(
        IContentTypeValidatorService validator, ContentTypeDefinition type, ContentTypeDefinition? stored, int maxFields)
    {
        // The cap was settled by the caller, including its one exception: a type already stored over
        // it may be written again at its size. The validator only knows the cap and checks no field
        // of a list over it, so an oversized list is checked a cap's worth of fields at a time.
        //
        // A field the stored type already holds with the same rules is passed as stored, so its rule
        // definitions are not checked: an export from this tenant has to write back into it,
        // including a rule saved before rules were checked.
        var carried = FieldsWithStoredRules(type, stored);

        var errors = type.Fields.Count <= maxFields
            ? validator.Validate(type.Name, type.DisplayName, type.Fields, carried).Errors
            : type.Fields.Chunk(maxFields)
                .SelectMany(chunk => validator.Validate(type.Name, type.DisplayName, chunk.ToList(), carried).Errors)
                .Distinct()
                .ToList();

        errors.AddRange(validator.ValidateRouteTemplate(type.RouteTemplate).Errors);
        errors.AddRange(validator.ValidateStructuredDataType(type.StructuredDataType).Errors);

        // Checked only for a type being created. A stored type keeps its rules.
        if (stored is null)
        {
            errors.AddRange(validator.ValidateUniqueness(type.Uniqueness, type.Fields, type.Lifecycle).Errors);
        }

        // The validator refuses a role two fields declare, but it saw an oversized list a chunk at
        // a time, and the two can sit in different chunks.
        if (type.Fields.Count > maxFields
            && type.Fields
                .Where(f => !string.IsNullOrEmpty(f.Role))
                .GroupBy(f => f.Role, StringComparer.Ordinal)
                .Any(g => g.Count() > 1))
        {
            errors.Add("a role is declared by more than one field, and one field of a type holds a role.");
        }

        // The validator does not check it. Two fields of one name let a definition declare a field
        // both Sensitive and Public, and public delivery serves it as the Public one.
        foreach (var repeated in type.Fields
                     .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            errors.Add($"field '{repeated.Key}' is declared more than once, ignoring case.");
        }

        // The fields a transition names are checked only for a lifecycle this write will store. A
        // stored one is kept as it is, and an export from this tenant has to write back.
        errors.AddRange(stored?.Lifecycle is null
            ? validator.ValidateLifecycle(type.Lifecycle, type.Fields).Errors
            : validator.ValidateLifecycle(type.Lifecycle).Errors);

        return errors;
    }

    /// <summary>
    /// A definition may not change the currency or scale a stored field declares, or whether the
    /// server generates a stored field's value.
    /// </summary>
    /// <remarks>
    /// The currency goes through <c>PUT /api/content-types/{name}/fields/{field}/currency</c>, which
    /// counts the entries holding an amount the new scale refuses. A field the stored type does not
    /// have is new and takes what the definition declares. Turning a stored field into a token would
    /// freeze values callers wrote as if the server had generated them, and the reverse would make
    /// every stored token writable by whoever may edit the entry.
    /// </remarks>
    private static IEnumerable<string> CurrencyAndTokenChanges(
        ContentTypeDefinition type, ContentTypeDefinition stored, Wording wording)
    {
        foreach (var current in stored.Fields)
        {
            var incoming = type.Fields.FirstOrDefault(f => f.Name.Equals(current.Name, StringComparison.OrdinalIgnoreCase));
            if (incoming is null)
            {
                continue;
            }

            if (!string.Equals(incoming.Currency, current.Currency, StringComparison.Ordinal) || incoming.Scale != current.Scale)
            {
                yield return $"field '{current.Name}' has a different currency or scale in {wording.Source}. "
                             + $"{wording.Writer} does not change a stored field's currency; use "
                             + $"PUT /api/content-types/{stored.Name}/fields/{current.Name}/currency.";
            }

            if (barakoCMS.Core.Validation.FieldTypeRegistry.IsServerGenerated(incoming)
                != barakoCMS.Core.Validation.FieldTypeRegistry.IsServerGenerated(current))
            {
                yield return $"field '{current.Name}' changes to or from the token type in {wording.Source}. "
                             + $"{wording.Writer} does not change whether the server generates a stored field's value.";
            }
        }
    }

    /// <summary>The rules that depend on how many entries the stored type holds.</summary>
    /// <remarks>
    /// No endpoint changes a stored type's lifecycle: permissions and workflows key on its transition
    /// names, and entries sit in its states. A definition may leave it out, which keeps it, or carry
    /// the same one. It may add one only to a type with no entries. And a required field with no
    /// default declares an invariant every entry already stored breaks, the add field rule.
    /// </remarks>
    private static IEnumerable<string> EntryRules(
        ContentTypeDefinition type, ContentTypeDefinition stored, int entries, Wording wording)
    {
        var writer = wording.Writer.ToLowerInvariant();

        if (type.Lifecycle is not null && !SameLifecycle(type.Lifecycle, stored.Lifecycle))
        {
            if (stored.Lifecycle is not null)
            {
                yield return $"{wording.Possessive} lifecycle differs from the stored one, and {writer} does not change a stored lifecycle.";
            }
            else if (entries > 0)
            {
                yield return $"it already has {Entries(entries)} and no lifecycle, and {writer} does not add one to a type with entries.";
            }
        }

        if (entries == 0)
        {
            yield break;
        }

        foreach (var field in type.Fields
                     .Where(f => f.IsRequired && f.DefaultValue is null)
                     .Where(f => !stored.Fields.Any(s => s.IsRequired && s.Name.Equals(f.Name, StringComparison.OrdinalIgnoreCase))))
        {
            yield return $"it already has {Entries(entries)}, so making '{field.Name}' required with no default "
                         + "would make them all invalid. Give the field a defaultValue, or leave it optional.";
        }
    }

    private static string Entries(int count) => count == 1 ? "1 entry" : $"{count} entries";

    /// <summary>
    /// Fields a stored transition names that the stored type has and the definition leaves out.
    /// </summary>
    /// <remarks>
    /// A write replaces a type's fields and keeps its lifecycle. A transition naming a field the type
    /// no longer has skips that name on every move, so dropping the field would switch a requirement
    /// off with nothing said. A name the stored type already lacks is not reported: that requirement
    /// is skipped today, and an export from this tenant has to write back.
    /// </remarks>
    private static IEnumerable<string> DroppedTransitionFields(
        ContentTypeDefinition type, ContentTypeDefinition stored, Wording wording)
    {
        if (stored.Lifecycle is null)
        {
            yield break;
        }

        foreach (var transition in stored.Lifecycle.Transitions)
        {
            var named = (transition.RequiredFields ?? [])
                .Concat(transition.OptionalFields ?? [])
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var name in named)
            {
                if (!stored.Fields.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    || type.Fields.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                yield return $"field '{name}' is named by the stored transition '{transition.Name}' and "
                             + $"{wording.Source} leaves it out, which would stop that transition asking for it. Keep the field.";
            }
        }
    }

    /// <summary>
    /// Fields the stored numbering rule or lookup names that the stored type has and the definition
    /// leaves out.
    /// </summary>
    /// <remarks>
    /// A write keeps the stored numbering rule and lookup (<see cref="MergeInto"/> carries neither).
    /// Dropping a field one of them names would write each new number under a name the type does not
    /// declare, or leave the lookup matching or returning nothing, with nothing said. The field
    /// change route refuses removing such a field for the same reason. A name the stored type already
    /// lacks is not reported, as for a transition.
    /// </remarks>
    private static IEnumerable<string> DroppedNumberingAndLookupFields(
        ContentTypeDefinition type, ContentTypeDefinition stored, Wording wording)
    {
        var named = new List<(string Field, string By)>();
        if (stored.Numbering is { } numbering)
        {
            named.Add((numbering.Field, "the stored numbering rule"));
        }

        if (stored.Lookup is { } lookup)
        {
            named.AddRange(barakoCMS.Core.Validation.FieldNames.LookupNames(lookup).Select(f => (f, "the stored lookup")));
        }

        foreach (var (field, by) in named.DistinctBy(n => (n.Field.ToLowerInvariant(), n.By)))
        {
            if (string.IsNullOrWhiteSpace(field)
                || !stored.Fields.Any(f => f.Name.Equals(field, StringComparison.OrdinalIgnoreCase))
                || type.Fields.Any(f => f.Name.Equals(field, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            yield return $"field '{field}' is named by {by} and {wording.Source} leaves it out, which would leave "
                         + $"{by} naming a field the type does not declare. Keep the field, or take it out of {by} first.";
        }
    }

    /// <summary>
    /// A definition may not change the uniqueness rules a stored type declares, or drop a field one
    /// of them compares.
    /// </summary>
    /// <remarks>
    /// Rules are set on a stored type with <c>PUT /api/content-types/{name}/uniqueness</c>, which
    /// counts the entries that already share their values before it adds one. A definition without
    /// rules keeps the stored ones. A field a stored rule compares and the definition leaves out
    /// would switch the rule off with nothing said.
    /// </remarks>
    private static IEnumerable<string> UniquenessChanges(
        ContentTypeDefinition type, ContentTypeDefinition stored, Wording wording)
    {
        var route = $"PUT /api/content-types/{stored.Name}/uniqueness";

        if (type.Uniqueness is { Count: > 0 } && !SameRules(type.Uniqueness, stored.Uniqueness))
        {
            yield return $"{wording.Possessive} uniqueness rules differ from the stored ones, and "
                         + $"{wording.Writer.ToLowerInvariant()} does not change a stored type's rules; use {route}.";
        }

        foreach (var rule in stored.Uniqueness ?? [])
        {
            foreach (var name in rule?.Fields ?? [])
            {
                if (string.Equals(name, UniquenessRule.CreatedByField, StringComparison.OrdinalIgnoreCase)
                    || !stored.Fields.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    || type.Fields.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                yield return $"field '{name}' is compared by the stored uniqueness rule '{rule!.Name}' and "
                             + $"{wording.Source} leaves it out, which would switch that rule off. Keep the field.";
            }
        }
    }

    private static bool SameRules(List<UniquenessRule> a, List<UniquenessRule>? b) =>
        b is not null
        && a.Count == b.Count
        && a.All(x => x is not null && b.Count(y => y is not null && SameRule(x, y)) == 1);

    private static bool SameRule(UniquenessRule a, UniquenessRule b) =>
        string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            string.IsNullOrWhiteSpace(a.WhenState) ? null : a.WhenState,
            string.IsNullOrWhiteSpace(b.WhenState) ? null : b.WhenState,
            StringComparison.OrdinalIgnoreCase)
        && (a.Fields ?? []).SequenceEqual(b.Fields ?? [], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A definition may not change who can read a stored field.
    /// </summary>
    /// <remarks>
    /// That is <c>PUT /api/content-types/{name}/fields/{field}/sensitivity</c>'s decision, behind its
    /// own capability, with an acknowledgement for lowering, its own audit entry, and a rebuild of
    /// the search text so a raised value stops matching anonymous search. A write carrying the change
    /// would skip all of that, so it is refused and the caller is pointed there. Leaving a non-Public
    /// field out counts as a change: a key the schema does not declare is served unmasked.
    /// </remarks>
    private static IEnumerable<string> SensitivityChanges(
        ContentTypeDefinition type, ContentTypeDefinition stored, Wording wording)
    {
        foreach (var current in stored.Fields)
        {
            // Duplicates are refused elsewhere; the first is enough here.
            var incoming = type.Fields.FirstOrDefault(f => f.Name.Equals(current.Name, StringComparison.OrdinalIgnoreCase));
            var route = $"PUT /api/content-types/{stored.Name}/fields/{current.Name}/sensitivity";

            if (incoming is null)
            {
                if (current.Sensitivity != SensitivityLevel.Public)
                {
                    yield return $"field '{current.Name}' is {current.Sensitivity} and {wording.Source} leaves it out, "
                                 + "which would serve the values already stored in it unmasked. Keep the "
                                 + $"field, or lower it with {route} first.";
                }

                continue;
            }

            if (incoming.Sensitivity != current.Sensitivity)
            {
                yield return $"field '{current.Name}' is {current.Sensitivity} and {wording.Source} makes it "
                             + $"{incoming.Sensitivity}. {wording.Writer} does not change who may read a field; use {route}.";
            }
            else if (current.Sensitivity != SensitivityLevel.Public
                     && (incoming.Mask != current.Mask || !SameRoles(incoming.VisibleToRoles, current.VisibleToRoles)))
            {
                yield return $"field '{current.Name}' has a different mask or visibleToRoles in {wording.Source}. "
                             + $"{wording.Writer} does not change who may read a field; use {route}.";
            }
        }
    }

    private static bool SameLifecycle(LifecycleDefinition a, LifecycleDefinition? b) =>
        b is not null
        && a.States.SequenceEqual(b.States, StringComparer.OrdinalIgnoreCase)
        && string.Equals(a.InitialState, b.InitialState, StringComparison.OrdinalIgnoreCase)
        && a.Transitions.Count == b.Transitions.Count
        && a.Transitions.Zip(b.Transitions).All(p =>
            string.Equals(p.First.Name, p.Second.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.First.From, p.Second.From, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.First.To, p.Second.To, StringComparison.OrdinalIgnoreCase)
            && SameFields(p.First.RequiredFields, p.Second.RequiredFields)
            && SameFields(p.First.OptionalFields, p.Second.OptionalFields));

    private static bool SameFields(List<string>? a, List<string>? b) =>
        new HashSet<string>(a ?? [], StringComparer.OrdinalIgnoreCase).SetEquals(b ?? []);

    /// <summary>
    /// The definition's own field instances whose type and validation rules are the ones the stored
    /// type holds for a field of that name. A rule depends on the field's type, so a definition that
    /// changes the type has changed what the rules mean.
    /// </summary>
    private static List<FieldDefinition> FieldsWithStoredRules(ContentTypeDefinition type, ContentTypeDefinition? stored)
    {
        var storedFields = stored?.Fields;
        if (storedFields is null)
        {
            return [];
        }

        return type.Fields
            .Where(field => storedFields.FirstOrDefault(
                    f => f is not null && string.Equals(f.Name, field.Name, StringComparison.OrdinalIgnoreCase))
                is { } held
                && string.Equals(field.Type, held.Type, StringComparison.OrdinalIgnoreCase)
                && SameRules(field.ValidationRules, held.ValidationRules))
            .ToList();
    }

    /// <summary>Same rule names, in the same case, each with the same JSON value.</summary>
    /// <remarks>
    /// Compared as JSON because the two sides are not the same CLR shape: a stored value comes back
    /// from the database and an incoming one from a request body, and one may be a
    /// <c>JsonElement</c> where the other is a number or a dictionary.
    /// </remarks>
    private static bool SameRules(Dictionary<string, object>? a, Dictionary<string, object>? b)
    {
        a ??= new Dictionary<string, object>();
        b ??= new Dictionary<string, object>();

        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (var (name, value) in a)
        {
            if (!b.TryGetValue(name, out var other))
            {
                return false;
            }

            var left = System.Text.Json.JsonSerializer.SerializeToElement<object?>(value);
            var right = System.Text.Json.JsonSerializer.SerializeToElement<object?>(other);

            if (!System.Text.Json.JsonElement.DeepEquals(left, right))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameRoles(List<string>? a, List<string>? b) =>
        (a ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b ?? []);
}
