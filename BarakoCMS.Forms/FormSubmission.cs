using barakoCMS.Models;

namespace BarakoCMS.Forms;

/// <summary>
/// What one visitor sent to one form. Personal data by construction: a contact form carries a
/// name and a way to reach the person, so the document is Sensitive from the moment it exists.
/// </summary>
/// <remarks>
/// Its own document, not a <c>Content</c> entry. Content is what public delivery serves, so a
/// submission stored as content would be one flag away from an anonymous listing, and no flag can
/// be set on a document that the delivery routes never query. Tenant scoped like the form it
/// belongs to.
/// </remarks>
public class FormSubmission
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid FormId { get; set; }

    public string FormName { get; set; } = string.Empty;

    /// <summary>Only the fields the definition declared, validated against their types.</summary>
    public Dictionary<string, object> Data { get; set; } = new();

    public SensitivityLevel Sensitivity { get; set; } = SensitivityLevel.Sensitive;

    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
}
