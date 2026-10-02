using barakoCMS.Models;
using Microsoft.AspNetCore.Http;

namespace barakoCMS.Core.Interfaces;

/// <summary>
/// Applies document- and field-level sensitivity to content data before it leaves the API.
/// One implementation, called explicitly by every read endpoint (Get, List, History) so the
/// masking always reaches the wire.
/// </summary>
public interface ISensitivityService
{
    /// <summary>
    /// Scrubs <paramref name="data"/> in place for the given content type and document sensitivity,
    /// based on the caller's roles and the content type's field schema. Returns <c>true</c> when the
    /// whole document is hidden from this caller (the caller may blank identifying fields such as the
    /// content type).
    /// </summary>
    ValueTask<bool> ApplyAsync(string contentType, SensitivityLevel level, IDictionary<string, object> data, HttpContext httpContext, CancellationToken ct = default);

    /// <summary>
    /// Enforces sensitivity on writes: a caller who may not <em>see</em> a field may not
    /// <em>set</em> it. Mutates <paramref name="incoming"/> in place — on update, a field the caller
    /// cannot write is reverted to its value in <paramref name="existing"/>; on create
    /// (<paramref name="existing"/> null) it is dropped. So masking cannot be bypassed by writing.
    /// </summary>
    ValueTask ApplyWriteAsync(string contentType, IDictionary<string, object> incoming, IReadOnlyDictionary<string, object>? existing, HttpContext httpContext, CancellationToken ct = default);

    /// <summary>
    /// The same rule against a definition the caller supplies rather than the stored one, for a
    /// write that brings the type with it, as an import does.
    /// </summary>
    /// <remarks>
    /// The default throws rather than falling back to the stored definition. A type the bundle
    /// creates is not stored yet, and falling back would find no fields to protect and drop nothing.
    /// </remarks>
    ValueTask ApplyWriteAsync(ContentTypeDefinition definition, IDictionary<string, object> incoming, IReadOnlyDictionary<string, object>? existing, HttpContext httpContext, CancellationToken ct = default)
        => throw new NotSupportedException(
            $"{GetType().Name} does not implement ApplyWriteAsync for a supplied definition.");

    /// <summary>
    /// Whether this caller reads the field's value unmasked.
    /// </summary>
    /// <remarks>
    /// A read endpoint asks before it lets a caller filter or sort on a field: which entries match
    /// tells the caller the value, so a field that <see cref="ApplyAsync"/> would mask for them must
    /// not be matched for them either. The default answers for a Public field only, so an
    /// implementation that does not override it refuses more, never less.
    /// </remarks>
    bool MaySeeField(FieldDefinition field, HttpContext httpContext)
        => field.Sensitivity == SensitivityLevel.Public;

    /// <summary>
    /// Whether this caller reads the data of a document at the given sensitivity.
    /// </summary>
    /// <remarks>
    /// The document-level half of <see cref="MaySeeField"/>: <see cref="ApplyAsync"/> clears the
    /// data of a document the caller may not see, and matching on that data would give it back one
    /// guess at a time. The default answers for a Public document only.
    /// </remarks>
    bool MaySeeDocument(SensitivityLevel level, HttpContext httpContext)
        => level == SensitivityLevel.Public;
}
