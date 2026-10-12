using barakoCMS.Models;

namespace barakoCMS.Core.Validation;

/// <summary>
/// What a content type's <see cref="LookupDefinition"/> may name: checked when it is set, and again
/// on every lookup.
/// </summary>
/// <remarks>
/// The second check is what makes "never Sensitive or Hidden" hold whatever the stored definition
/// says. A field made Sensitive after the lookup was set, or a definition stored by an import, is
/// left out of the answer rather than returned.
/// </remarks>
internal static class LookupRules
{
    public const int MaxVerifyFields = 5;
    public const int MaxReturnFields = 20;
    public const int MaxRevealFields = 5;
    public const int MaxNameLength = 64;

    /// <summary>The longest reference or identifier a caller may send.</summary>
    public const int MaxValueLength = 200;

    /// <summary>Why a definition cannot be stored on this type, or empty when it can.</summary>
    /// <param name="settingType">The singleton the reveal reads, already loaded, or null when there is none.</param>
    public static List<string> Errors(LookupDefinition lookup, ContentTypeDefinition def, ContentTypeDefinition? settingType)
    {
        var errors = new List<string>();

        if (Field(def, lookup.MatchField) is null)
            errors.Add($"matchField '{lookup.MatchField}' is not a field of '{def.Name}'.");

        foreach (var name in lookup.VerifyFields)
        {
            if (Field(def, name) is null)
                errors.Add($"verifyFields names '{name}', which is not a field of '{def.Name}'.");
            else if (string.Equals(name, lookup.MatchField, StringComparison.OrdinalIgnoreCase))
                errors.Add($"'{name}' is the match field and cannot also verify it: the reference alone would then return the entry.");
        }

        foreach (var name in lookup.ReturnFields)
        {
            var field = Field(def, name);
            if (field is null)
                errors.Add($"returnFields names '{name}', which is not a field of '{def.Name}'.");
            else if (!IsReturnable(field))
                errors.Add($"'{field.Name}' is {Describe(field)} and cannot be returned by an anonymous lookup.");
        }

        if (lookup.Reveal is { } reveal)
        {
            foreach (var name in reveal.Fields)
            {
                var field = Field(def, name);
                if (field is null)
                    errors.Add($"reveal.fields names '{name}', which is not a field of '{def.Name}'.");
                else if (!IsRevealable(field))
                    errors.Add($"'{field.Name}' is {Describe(field)}. A reveal may name a Public field or a token, nothing else.");
            }

            if (settingType is null)
            {
                errors.Add($"reveal.settingType '{reveal.SettingType}' is not a content type.");
            }
            else if (!settingType.IsSingleton)
            {
                errors.Add($"reveal.settingType '{settingType.Name}' is not a singleton, so it has no one value to read.");
            }
            else if (Field(settingType, reveal.SettingField) is not { } setting)
            {
                errors.Add($"reveal.settingField '{reveal.SettingField}' is not a field of '{settingType.Name}'.");
            }
            else if (!IsBool(setting))
            {
                errors.Add($"reveal.settingField '{setting.Name}' is not a bool field.");
            }
        }

        return errors;
    }

    /// <summary>Whether a found entry may hand this field to an anonymous caller.</summary>
    public static bool IsReturnable(FieldDefinition field) =>
        field.Sensitivity == SensitivityLevel.Public && !TokenFields.IsToken(field.Type);

    /// <summary>A Public field, or a token: the credential the entry holds for whoever proved who they are.</summary>
    public static bool IsRevealable(FieldDefinition field) =>
        IsReturnable(field) || TokenFields.IsToken(field.Type);

    public static bool IsBool(FieldDefinition field) =>
        string.Equals(field.Type, "bool", StringComparison.OrdinalIgnoreCase)
        || string.Equals(field.Type, "boolean", StringComparison.OrdinalIgnoreCase);

    public static FieldDefinition? Field(ContentTypeDefinition def, string? name) =>
        string.IsNullOrEmpty(name)
            ? null
            : def.Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string Describe(FieldDefinition field) =>
        TokenFields.IsToken(field.Type) ? "a token" : field.Sensitivity.ToString();
}
