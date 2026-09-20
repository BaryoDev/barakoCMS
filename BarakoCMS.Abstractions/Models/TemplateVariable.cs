namespace barakoCMS.Models;

/// <summary>
/// Information about an available template variable.
/// </summary>
public class TemplateVariable
{
    /// <summary>
    /// The variable name with template syntax (e.g., "{{status}}").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable description.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Example value.
    /// </summary>
    public string Example { get; set; } = string.Empty;

    /// <summary>
    /// Data type (string, number, boolean, datetime).
    /// </summary>
    public string Type { get; set; } = "string";
}

/// <summary>
/// Collection of available template variables.
/// </summary>
public class TemplateVariableCollection
{
    /// <summary>
    /// System-level variables (status, contentType, id, etc.).
    /// </summary>
    public List<TemplateVariable> SystemVariables { get; set; } = new();

    /// <summary>
    /// Content-specific data fields.
    /// </summary>
    public List<TemplateVariable> DataFields { get; set; } = new();
}
