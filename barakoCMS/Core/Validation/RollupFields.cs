using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Queries.Run;
using barakoCMS.Infrastructure.Auth;
using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Core.Validation;

/// <summary>Who saves the type, for the checks that ask whether they may read what a rollup summarises.</summary>
/// <remarks>Null in place of one is the system, which config as code and a seeder are.</remarks>
internal sealed record RollupCaller(
    System.Security.Claims.ClaimsPrincipal Principal,
    ISensitivityService Sensitivity,
    IPermissionResolver Permissions);

/// <summary>
/// A field the server fills from the entries of another type that point at its entry: checked when
/// a type is saved, and when a change to the other type would break it.
/// </summary>
/// <remarks>
/// The checks that need one type's fields are in <see cref="DefinitionErrors"/>, which the type
/// validator runs. The ones that need another type are in <see cref="DefinitionErrorsAsync"/>,
/// which every path that stores a type calls. See RollupMarks for how a value is kept.
/// </remarks>
internal static class RollupFields
{
    public static readonly IReadOnlyList<string> Functions = ["count", "sum", "min", "max", "avg"];

    public const int MaxRound = 10;

    /// <summary>The window key that means the entry's created time, whatever a field of the type is called.</summary>
    public const string CreatedAt = "createdAt";

    public static bool IsCreatedAt(string? key) => string.Equals(key?.Trim(), CreatedAt, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The window of a rollup: its key and its word in the casing of the word list. Null when the
    /// rollup has no window, or has one the validator refuses, which never reaches a query.
    /// </summary>
    public static (string Key, string Word)? WindowOf(RollupDefinition rollup)
    {
        if (rollup.Where is not { Count: 1 } where)
            return null;

        var entry = where.First();
        return !Blank(entry.Key) && RelativeDateWindow.Canonical(entry.Value) is { } word
            ? (entry.Key.Trim(), word)
            : null;
    }

    /// <summary>The window field of a rollup, when it is a field of the summarised type and not createdAt.</summary>
    public static string? WindowFieldOf(RollupDefinition rollup) =>
        WindowOf(rollup) is { } window && !IsCreatedAt(window.Key) ? window.Key : null;

    public static bool IsRollup(FieldDefinition field) => field.Rollup is not null;

    public static string FnOf(RollupDefinition rollup) => (rollup.Fn ?? string.Empty).Trim().ToLowerInvariant();

    public static string RefusedMessage(FieldDefinition field) =>
        $"Field '{field.DisplayName}' ({field.Name}) is a rollup kept by the server and cannot be set. Leave it out.";

    /// <summary>The type name with the aliases the registry accepts folded in, lower case.</summary>
    public static string Canon(string? type) => (type ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "integer" or "number" => "int",
        var other => other,
    };

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The declared types a rollup of this function may have.</summary>
    private static string[] DeclarableTypes(string fn) => fn switch
    {
        "count" => ["int"],
        "sum" => ["int", "decimal", "money"],
        "avg" => ["decimal", "money"],
        _ => ["int", "decimal", "money", "date", "datetime"],
    };

    /// <summary>What is wrong with the rollup fields of a type that its own fields alone show.</summary>
    public static List<string> DefinitionErrors(IReadOnlyCollection<FieldDefinition> fields)
    {
        var errors = new List<string>();
        foreach (var field in fields.Where(f => f is not null && IsRollup(f) && !Blank(f.Name)))
            errors.AddRange(FieldErrors(field));

        return errors;
    }

    private static IEnumerable<string> FieldErrors(FieldDefinition field)
    {
        var def = field.Rollup!;
        var name = field.Name;
        var fn = FnOf(def);

        if (ComputedFields.IsComputed(field))
            yield return $"Field '{name}' is a rollup and is computed. A field is one or the other.";

        if (!Functions.Contains(fn))
        {
            yield return $"Field '{name}' rolls up with '{def.Fn}', which is not a function. Use {string.Join(", ", Functions)}.";
            yield break;
        }

        foreach (var error in ShapeErrors(field, def, fn))
            yield return error;

        if (!DeclarableTypes(fn).Contains(Canon(field.Type)))
            yield return $"Field '{name}' is a {fn} rollup and must be of type {string.Join(", ", DeclarableTypes(fn))}, not '{field.Type}'.";

        foreach (var error in ServerSetErrors(field))
            yield return error;
    }

    private static IEnumerable<string> ShapeErrors(FieldDefinition field, RollupDefinition def, string fn)
    {
        var name = field.Name;
        if (Blank(def.From))
            yield return $"Field '{name}' is a rollup and must name the content type it summarises, in rollup.from.";

        if (Blank(def.Via))
            yield return $"Field '{name}' is a rollup and must name the reference field that points at it, in rollup.via.";

        if (fn == "count" && !Blank(def.Field))
            yield return $"Field '{name}' counts entries, so it takes no rollup.field.";

        if (fn != "count" && Blank(def.Field))
            yield return $"Field '{name}' rolls up with {fn} and must name the field it summarises, in rollup.field.";

        if (def.Round is { } digits && fn is not ("sum" or "avg"))
            yield return $"Field '{name}' rolls up with {fn}, so it takes no round.";
        else if (def.Round is { } places && (places < 0 || places > MaxRound))
            yield return $"Field '{name}' rounds to {places} places, and rounding is 0 to {MaxRound}.";

        foreach (var error in WhereErrors(field, def))
            yield return error;
    }

    private static IEnumerable<string> WhereErrors(FieldDefinition field, RollupDefinition def)
    {
        if (def.Where is null)
            yield break;

        var name = field.Name;
        if (def.Where.Count != 1)
        {
            yield return $"Field '{name}' has a where with {def.Where.Count} keys, and a rollup window has exactly one: "
                + $"{CreatedAt}, or a date or datetime field of '{def.From}'.";
            yield break;
        }

        var (key, value) = def.Where.First();
        if (Blank(key))
            yield return $"Field '{name}' has a where with no field. Name {CreatedAt}, or a date or datetime field of '{def.From}'.";

        if (!RelativeDateWindow.IsWord(value))
            yield return $"Field '{name}' windows on '{key}' with '{value}', which is not a window. Use {string.Join(", ", RelativeDateWindow.Words)}.";
    }

    private static IEnumerable<string> ServerSetErrors(FieldDefinition field)
    {
        var name = field.Name;
        if (field.Multiple)
            yield return $"Field '{name}' is a rollup and holds one value, so it cannot hold a list.";

        if (field.IsRequired)
            yield return $"Field '{name}' is a rollup kept by the server, so it cannot be required of a caller.";

        if (field.DefaultValue is not null)
            yield return $"Field '{name}' is a rollup kept by the server, so it takes no default value.";

        if (PublicFields.IsOwnerHidden(field))
            yield return $"Field '{name}' is a rollup and cannot be hidden by its owner: the server, not the owner, sets it.";
    }

    /// <summary>Whether a rule of the type's lines compares a rollup, which lags the write the rule would judge.</summary>
    public static IEnumerable<string> LinesErrors(ContentTypeDefinition header, ContentTypeDefinition? child, LinesDefinition? lines)
    {
        if (lines is null)
            yield break;

        if (header.Fields.FieldNamed(lines.TotalField) is { Rollup: not null } total)
            yield return $"'{total.Name}' is a rollup, so it cannot be the lines total: the total is compared when an entry is written and a rollup follows later.";

        if (child?.Fields.FieldNamed(lines.LineAmountField) is { Rollup: not null } amount)
            yield return $"'{amount.Name}' on '{child.Name}' is a rollup, so it cannot be the line amount: the total is compared when an entry is written and a rollup follows later.";
    }

    /// <summary>
    /// What is wrong with the rollups of <paramref name="definition"/> that another type decides,
    /// and with a rollup it names as its lines total or line amount.
    /// </summary>
    /// <param name="session">The session whose transaction holds the locks.</param>
    /// <param name="definition">The type being saved.</param>
    /// <param name="pendingTypes">Types saved in the same set, which count as stored: a header and its lines arrive together.</param>
    /// <param name="caller">Who saves it, or null for the system.</param>
    /// <param name="ct">Cancellation.</param>
    /// <remarks>
    /// Takes the type's own lock and the other types' shared locks before it reads them, so a change
    /// to a type it summarises either finished before this read or waits for this save.
    /// </remarks>
    public static async Task<List<string>> DefinitionErrorsAsync(
        IDocumentSession session,
        ContentTypeDefinition definition,
        IReadOnlyCollection<ContentTypeDefinition>? pendingTypes,
        RollupCaller? caller,
        CancellationToken ct)
    {
        var rollups = definition.Fields.Where(f => f is not null && IsRollup(f) && !Blank(f.Name) && !Blank(f.Rollup!.From)).ToList();
        if (rollups.Count == 0 && definition.Lines is null)
            return [];

        await FieldChanges.LockTypesAsync(session, definition.Name, rollups.Select(r => r.Rollup!.From.Trim()), ct);
        var stored = await session.Query<ContentTypeDefinition>().ToListAsync(ct);

        ContentTypeDefinition? Resolve(string? name) =>
            (pendingTypes ?? []).FirstOrDefault(t => ContentTypeName.Same(t.Name, name))
            ?? (ContentTypeName.Same(definition.Name, name) ? definition : null)
            ?? stored.FirstOrDefault(t => ContentTypeName.Same(t.Name, name));

        var errors = new List<string>();
        var child = definition.Lines is { } lines ? Resolve(lines.ContentType) : null;
        errors.AddRange(LinesErrors(definition, child, definition.Lines));
        errors.AddRange(HeadersNamingLineAmount(definition, stored, pendingTypes));

        var owned = rollups.Count > 0 && await AccountContentTypes.FindAsync(session, definition.Name, ct) is not null;
        foreach (var field in rollups)
        {
            if (owned)
                errors.Add($"Field '{field.Name}' is a rollup on '{definition.Name}', which the account owns. A rollup is not supported on an account-owned type.");
            else
                errors.AddRange(await CrossErrorsAsync(session, definition, field, Resolve, caller, ct));
        }

        return errors;
    }

    /// <summary>Headers whose lines name a rollup of this type as their line amount.</summary>
    private static IEnumerable<string> HeadersNamingLineAmount(
        ContentTypeDefinition definition, IEnumerable<ContentTypeDefinition> stored, IReadOnlyCollection<ContentTypeDefinition>? pending)
    {
        foreach (var header in stored.Concat(pending ?? []).Where(h => h.Lines is { } l && ContentTypeName.Same(l.ContentType, definition.Name)))
        {
            if (definition.Fields.FieldNamed(header.Lines!.LineAmountField) is { Rollup: not null } amount)
                yield return $"'{amount.Name}' is a rollup, and the lines of '{header.Name}' compare it as the line amount. Take it out of the lines first.";
        }
    }

    /// <summary>
    /// What a change to <paramref name="typeName"/> would break in the rollups that summarise it:
    /// the other types' rollups are checked against <paramref name="proposed"/>, or against no type
    /// at all when it is null (the type is being deleted).
    /// </summary>
    /// <remarks>
    /// Takes the type's lock first, so a type that gains a rollup over it commits before this read
    /// or waits for the change. Every writer of a type's fields calls it before it stores.
    /// </remarks>
    public static async Task<List<string>> BreaksAsync(
        IDocumentSession session, string typeName, ContentTypeDefinition? proposed, CancellationToken ct,
        IReadOnlyCollection<ContentTypeDefinition>? pending = null)
    {
        await FieldChanges.LockTypeAsync(session, typeName, ct);
        var errors = new List<string>();
        var types = (await session.Query<ContentTypeDefinition>().ToListAsync(ct))
            .Select(t => pending?.FirstOrDefault(p => ContentTypeName.Same(p.Name, t.Name)) ?? t);
        foreach (var header in types)
        {
            foreach (var rollup in header.Fields.Where(f => f is not null && IsRollup(f) && ContentTypeName.Same(f.Rollup!.From, typeName)))
            {
                var label = $"{header.Name}.{rollup.Name}";
                if (proposed is null)
                    errors.Add($"The rollup {label} summarises '{typeName}', so it cannot be deleted. Remove the rollup first.");
                else
                    errors.AddRange(BreakErrors(header, rollup, proposed).Select(e => $"This would break the rollup {label}. {e}"));
            }
        }

        return errors;
    }

    /// <summary>
    /// What changing one field of a type to <paramref name="proposed"/> would newly break: the
    /// rollups over this type, and, when the field is a rollup, its own cross-type checks.
    /// Only what the change introduces, so a type with an older problem can still be fixed.
    /// </summary>
    public static async Task<List<string>> IntroducedAsync(
        IDocumentSession session, ContentTypeDefinition def, FieldDefinition field, FieldDefinition proposed,
        RollupCaller? caller, CancellationToken ct)
    {
        var after = new ContentTypeDefinition
        {
            Name = def.Name,
            BranchScoped = def.BranchScoped,
            Fields = def.Fields.Select(f => f == field ? proposed : f).ToList(),
            Lines = def.Lines,
        };

        var errors = new List<string>();
        var already = await BreaksAsync(session, def.Name, def, ct);
        errors.AddRange((await BreaksAsync(session, def.Name, after, ct)).Where(e => !already.Contains(e)));
        if (proposed.Rollup is not null)
        {
            var before = await DefinitionErrorsAsync(session, def, null, caller, ct);
            errors.AddRange((await DefinitionErrorsAsync(session, after, null, caller, ct)).Where(e => !before.Contains(e)));
        }

        return errors;
    }

    private static List<string> BreakErrors(ContentTypeDefinition header, FieldDefinition rollup, ContentTypeDefinition from)
    {
        var errors = ViaErrors(header, rollup, from);
        if (errors.Count == 0 && FnOf(rollup.Rollup!) != "count")
            errors.AddRange(SourceErrors(rollup, from, FnOf(rollup.Rollup!)));

        if (errors.Count == 0)
            errors.AddRange(WindowErrors(rollup, from));

        return errors;
    }

    private static async Task<List<string>> CrossErrorsAsync(
        IDocumentSession session,
        ContentTypeDefinition definition,
        FieldDefinition field,
        Func<string?, ContentTypeDefinition?> resolve,
        RollupCaller? caller,
        CancellationToken ct)
    {
        var def = field.Rollup!;
        var name = field.Name;
        if (resolve(def.From) is not { } from)
            return [$"Field '{name}' rolls up '{def.From}', which is not a content type."];

        if (await AccountContentTypes.FindAsync(session, from.Name, ct) is not null)
            return [$"Field '{name}' rolls up '{from.Name}', which the account owns. A rollup is not supported over an account-owned type."];

        var errors = ViaErrors(definition, field, from);
        var fn = FnOf(def);
        if (fn != "count")
            errors.AddRange(SourceErrors(field, from, fn));

        errors.AddRange(WindowErrors(field, from));

        if (errors.Count == 0 && caller is not null)
            errors.AddRange(await CallerErrorsAsync(session, field, from, caller, ct));

        return errors;
    }

    private static List<string> ViaErrors(ContentTypeDefinition definition, FieldDefinition field, ContentTypeDefinition from)
    {
        if (from.BranchScoped)
        {
            return [$"Field '{field.Name}' rolls up '{from.Name}', which is branch scoped. A stored count covers the entries of every branch, "
                + "and a branch scoped type is only read within its branch."];
        }

        var via = from.Fields.FieldNamed(field.Rollup!.Via);
        if (via is null)
            return [$"Field '{field.Name}' rolls up through '{field.Rollup.Via}', which '{from.Name}' does not declare."];

        if (!Same(via.Type, "reference") || via.Multiple || !ContentTypeName.Same(via.ReferenceType, definition.Name))
            return [$"Field '{field.Name}' rolls up through '{via.Name}', which must be a single reference to '{definition.Name}' on '{from.Name}'."];

        // The count of entries that point at a header is itself the reference's information.
        if (SensitivityRank.Exposure(field.Sensitivity) > SensitivityRank.Exposure(via.Sensitivity))
        {
            return [$"Field '{field.Name}' is {field.Sensitivity} and rolls up through '{via.Name}', which is {via.Sensitivity}. "
                + "A rollup may not be more widely readable than the reference it goes through."];
        }

        return ComputedFields.ReadAudienceErrors(field, via)
            .Select(e => e.Replace(" computes from ", " rolls up through ", StringComparison.Ordinal)).ToList();
    }

    private static List<string> SourceErrors(FieldDefinition field, ContentTypeDefinition from, string fn)
    {
        var source = from.Fields.FieldNamed(field.Rollup!.Field);
        if (source is null)
            return [$"Field '{field.Name}' rolls up '{field.Rollup.Field}', which '{from.Name}' does not declare."];

        var errors = new List<string>();
        var kind = Canon(source.Type);
        var summable = fn is "sum" or "avg" ? new[] { "int", "decimal", "money" } : ["int", "decimal", "money", "date", "datetime"];
        if (source.Multiple || !summable.Contains(kind))
            errors.Add($"Field '{field.Name}' rolls up {fn} of '{source.Name}', which is a {source.Type}. {fn} takes {string.Join(", ", summable)}.");

        if (IsRollup(source) || ReadsRollup(from, source, []))
            errors.Add($"Field '{field.Name}' rolls up '{source.Name}', which is or reads a rollup. A rollup does not read another rollup.");

        if (PublicFields.IsOwnerHidden(source))
            errors.Add($"Field '{field.Name}' rolls up '{source.Name}', which its owner can hide, and a rollup would show what the owner hides.");

        if (SensitivityRank.Exposure(field.Sensitivity) > SensitivityRank.Exposure(source.Sensitivity))
            errors.Add($"Field '{field.Name}' is {field.Sensitivity} and rolls up '{source.Name}', which is {source.Sensitivity}. "
                + "A rollup may not be more widely readable than what it summarises.");

        errors.AddRange(ComputedFields.ReadAudienceErrors(field, source).Select(e => e.Replace(" computes from ", " rolls up ", StringComparison.Ordinal)));
        errors.AddRange(TypeMatchErrors(field, source, fn));
        return errors;
    }

    /// <summary>
    /// What is wrong with the date field a rollup windows on. createdAt is the entry's own created
    /// time and needs none of this. The field's level matters even for a count, because a windowed
    /// count tells which entries fall in the window.
    /// </summary>
    private static List<string> WindowErrors(FieldDefinition field, ContentTypeDefinition from)
    {
        if (WindowFieldOf(field.Rollup!) is not { } key)
            return [];

        if (from.Fields.FieldNamed(key) is not { } window)
            return [$"Field '{field.Name}' windows on '{key}', which '{from.Name}' does not declare."];

        var errors = new List<string>();
        if (window.Multiple || Canon(window.Type) is not ("date" or "datetime"))
            errors.Add($"Field '{field.Name}' windows on '{window.Name}', which is a {window.Type}. A window takes a date or datetime field.");

        if (PublicFields.IsOwnerHidden(window))
            errors.Add($"Field '{field.Name}' windows on '{window.Name}', which its owner can hide, and a rollup would show which entries fall in the window.");

        if (SensitivityRank.Exposure(field.Sensitivity) > SensitivityRank.Exposure(window.Sensitivity))
        {
            errors.Add($"Field '{field.Name}' is {field.Sensitivity} and windows on '{window.Name}', which is {window.Sensitivity}. "
                + "A rollup may not be more widely readable than the field that decides which entries it counts.");
        }

        errors.AddRange(ComputedFields.ReadAudienceErrors(field, window).Select(e => e.Replace(" computes from ", " windows on ", StringComparison.Ordinal)));
        return errors;
    }

    private static IEnumerable<string> TypeMatchErrors(FieldDefinition field, FieldDefinition source, string fn)
    {
        var declared = Canon(field.Type);
        var kind = Canon(source.Type);
        var expected = fn switch
        {
            "count" => "int",
            "avg" => kind == "money" ? "money" : "decimal",
            _ => kind,
        };

        if (declared != expected)
            yield return $"Field '{field.Name}' rolls up {fn} of '{source.Name}' ({source.Type}), so it must be of type {expected}, not '{field.Type}'.";

        if (kind == "money" && declared == "money" && !Same(field.Currency, source.Currency))
            yield return $"Field '{field.Name}' must have the currency of '{source.Name}', which is {source.Currency ?? "not set"}.";
    }

    /// <summary>Whether a computed field reads a rollup, directly or through another computed field.</summary>
    private static bool ReadsRollup(ContentTypeDefinition type, FieldDefinition field, HashSet<string> seen)
    {
        if (field.Computed is null || !seen.Add(field.Name.ToLowerInvariant()))
            return false;

        return type.Fields.Any(f => f is not null && !Same(f.Name, field.Name)
            && ComputedFields.Reads(field.Computed, f.Name)
            && (IsRollup(f) || ReadsRollup(type, f, seen)));
    }

    private static async Task<List<string>> CallerErrorsAsync(
        IDocumentSession session, FieldDefinition field, ContentTypeDefinition from, RollupCaller caller, CancellationToken ct)
    {
        // Loaded the way every acting check is, so a principal key is judged too. A caller that
        // resolves to no user is refused, never skipped.
        if (await ActingCaller.LoadAsync(session, caller.Principal, ct) is not { } user)
            return [$"Who is saving '{field.Name}' could not be identified, so a rollup over '{from.Name}' cannot be checked against what they may read."];

        var errors = new List<string>();
        if (!await caller.Permissions.CanPerformActionAsync(user, from.Name, "read", null, ct))
            errors.Add($"You may not read '{from.Name}', so you may not roll it up in '{field.Name}'.");

        var read = new List<string>();
        if (from.Fields.FieldNamed(field.Rollup!.Via) is { } via)
            read.Add(via.Name);
        if (from.Fields.FieldNamed(field.Rollup.Field) is { } source)
            read.Add(source.Name);
        if (WindowFieldOf(field.Rollup) is { } windowKey && from.Fields.FieldNamed(windowKey) is { } window)
            read.Add(window.Name);

        foreach (var name in read)
        {
            if (!await caller.Sensitivity.MayReadFieldAsync(from.Name, from.Fields.FieldNamed(name)!, null, caller.Principal, ct))
                errors.Add($"You may not read '{from.Name}.{name}', so you may not roll it up in '{field.Name}'.");
        }

        return errors;
    }
}
