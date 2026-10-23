using barakoCMS.Models;

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
        + "not read as a second entry. Removal planned for barakoCMS 6.0.")]
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
