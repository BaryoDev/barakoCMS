namespace BarakoCMS.Forms;

/// <summary>
/// Marks one content type as accepting anonymous submissions. A type with no row is not a form.
/// </summary>
/// <remarks>
/// Owned by the module rather than a flag on <c>ContentTypeDefinition</c>, so core does not change
/// and a deployment without the module has no flag that does nothing.
/// </remarks>
public sealed class PublicForm
{
    /// <summary>The content type name, which is also the form's slug in the public route.</summary>
    public string ContentType { get; set; } = string.Empty;

    public DateTimeOffset EnabledAt { get; set; }

    public Guid EnabledBy { get; set; }
}
