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

    /// <summary>
    /// The email field a visitor has to prove they can read mail at before a submission is accepted,
    /// or null when the form does not verify. A row stored before this existed reads null.
    /// </summary>
    public string? VerifyEmailField { get; set; }

    /// <summary>
    /// The singleton content type that holds the form's opening and closing dates, or null when the
    /// form has no window. A row stored before this existed reads null.
    /// </summary>
    public string? WindowContentType { get; set; }

    /// <summary>The date field of <see cref="WindowContentType"/> before which the form refuses submissions, or null.</summary>
    public string? OpensAtField { get; set; }

    /// <summary>The date field of <see cref="WindowContentType"/> from which the form refuses submissions, or null.</summary>
    public string? ClosesAtField { get; set; }
}
