using System.Security.Cryptography;
using System.Text.Json;
using barakoCMS.Models;

namespace barakoCMS.Core.Validation;

/// <summary>
/// The <c>token</c> field type: a random value the server generates when an entry is created and
/// that no caller can set or change.
/// </summary>
/// <remarks>
/// A token is a credential for the entry that holds it, so three rules follow, and each is applied
/// in one place.
///
/// The value is the server's. <c>ContentWriter</c> fills it on create and keeps the stored one on
/// every later write, so a route that forgets the rule still cannot break it. The write path
/// sensitivity rule drops a sent value before validation and hooks see it.
///
/// It is never Public. Every reader of entry data for a caller without roles (delivery, public
/// search, feeds, webhooks, connector requests, forms) takes the fields a type marks Public, so a
/// token that cannot be Public reaches none of them. A definition is refused here, and the
/// endpoints store one sent as Public, which is what leaving sensitivity out sends, as Hidden.
///
/// It is random enough that guessing is not a way in. <see cref="RandomNumberGenerator"/> picks
/// each character from 32, five bits each, so the shortest token allowed is 80 bits.
/// </remarks>
internal static class TokenFields
{
    public const string TypeName = "token";

    public const int DefaultLength = 32;
    public const int MinLength = 16;
    public const int MaxLength = 128;

    /// <summary>
    /// Crockford's base 32 in lower case: the digits and the letters without i, l, o and u.
    /// </summary>
    /// <remarks>
    /// Thirty-two characters, so each is exactly five bits and picking one has no bias. One case
    /// and no look-alike letters, because a token is printed and typed back.
    /// </remarks>
    public const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    public static bool IsToken(string? type) =>
        string.Equals(type, TypeName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The length a new token of this field gets. A stored length outside the range reads as the default.</summary>
    public static int LengthOf(FieldDefinition field) =>
        field.TokenLength is { } length && length >= MinLength && length <= MaxLength ? length : DefaultLength;

    public static string Generate(int length) => RandomNumberGenerator.GetString(Alphabet, length);

    /// <summary>Whether a stored value is no token at all, so the entry is still owed one.</summary>
    public static bool IsBlank(object? value) => value switch
    {
        null => true,
        string text => string.IsNullOrWhiteSpace(text),
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => true,
        JsonElement { ValueKind: JsonValueKind.String } element => string.IsNullOrWhiteSpace(element.GetString()),
        _ => false,
    };

    /// <summary>
    /// Raises a token field declared Public to Hidden, before the definition is checked and stored.
    /// </summary>
    /// <remarks>
    /// Public is the value a definition holds when sensitivity was left out, so this is the default
    /// of the type and not a correction. A field already Sensitive or Hidden is left as declared.
    /// </remarks>
    public static void ApplyDefaults(IEnumerable<FieldDefinition?>? fields)
    {
        foreach (var field in fields ?? [])
        {
            if (field is not null && IsToken(field.Type) && field.Sensitivity == SensitivityLevel.Public)
                field.Sensitivity = SensitivityLevel.Hidden;
        }
    }

    /// <summary>What is wrong with a token field's definition, for the type validator.</summary>
    public static List<string> DefinitionErrors(FieldDefinition field)
    {
        var errors = new List<string>();

        if (!IsToken(field.Type))
        {
            if (field.TokenLength is not null)
                errors.Add($"Field '{field.Name}' declares a token length but is of type '{field.Type}', not token.");

            return errors;
        }

        if (field.TokenLength is { } length && (length < MinLength || length > MaxLength))
        {
            errors.Add($"Field '{field.Name}' has a token length of {length}, and a token is "
                + $"{MinLength} to {MaxLength} characters long.");
        }

        if (field.Sensitivity == SensitivityLevel.Public)
        {
            errors.Add($"Field '{field.Name}' is a token and cannot be Public. Declare it Hidden or "
                + "Sensitive, with visibleToRoles for the roles that may read it.");
        }

        if (field.IsRequired)
        {
            errors.Add($"Field '{field.Name}' is a token, which the server fills in, so it cannot be "
                + "required of a caller.");
        }

        if (field.DefaultValue is not null)
        {
            errors.Add($"Field '{field.Name}' is a token, which the server generates, so it takes no "
                + "default value.");
        }

        return errors;
    }
}
