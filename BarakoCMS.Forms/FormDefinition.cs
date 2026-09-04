using System.Text.RegularExpressions;

namespace BarakoCMS.Forms;

/// <summary>
/// A form a tenant accepts submissions for: which fields it has, who to tell, and whether it is
/// taking submissions at all. Tenant scoped, so two tenants can each have a "contact".
/// </summary>
public class FormDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The URL segment: <c>POST /api/public/forms/{name}</c>. Lower-case slug.</summary>
    public string Name { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public List<FormField> Fields { get; set; } = new();

    /// <summary>Addresses that get one email per submission. Empty means nobody is told.</summary>
    public List<string> NotifyAddresses { get; set; } = new();

    /// <summary>Off means the public endpoint answers 404, as if the form did not exist.</summary>
    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class FormField
{
    public string Name { get; set; } = string.Empty;

    /// <summary>A type from core's <c>FieldTypeRegistry</c>, which is what validates the value.</summary>
    public string Type { get; set; } = "string";

    public bool Required { get; set; }
}

/// <summary>The shape rules for names, shared by the create and update validators.</summary>
public static partial class FormRules
{
    public const int MaxFields = 50;
    public const int MaxNotifyAddresses = 20;
    public const int MaxNameLength = 64;
    public const int MaxDisplayNameLength = 200;

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    public static partial Regex FormName();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$")]
    public static partial Regex FieldName();
}
