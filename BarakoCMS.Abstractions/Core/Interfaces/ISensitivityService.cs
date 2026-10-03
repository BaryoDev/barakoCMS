using System.Security.Claims;
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
    /// implementation that does not override it refuses more, never less. That holds only if the
    /// implementation's <see cref="ApplyAsync"/> never masks a field declared Public: one that
    /// does has to override this, or the default lets a caller filter on a field it masks.
    ///
    /// Asynchronous because the answer can need the caller's stored roles. It takes the request's
    /// principal as it is, and an implementation looks up whatever it decides from.
    /// </remarks>
    ValueTask<bool> MaySeeFieldAsync(FieldDefinition field, ClaimsPrincipal user, CancellationToken ct = default)
        => ValueTask.FromResult(field.Sensitivity == SensitivityLevel.Public);

    /// <summary>
    /// <see cref="ApplyAsync(string, SensitivityLevel, IDictionary{string, object}, HttpContext, CancellationToken)"/>
    /// for one stored entry, so a rule that grants that entry decides which of its fields are shown.
    /// </summary>
    /// <remarks>
    /// The other overload knows the type and not the entry, and answers with the fields shown on
    /// every entry of the type the caller may read, which can be fewer. The default calls it.
    /// </remarks>
    ValueTask<bool> ApplyAsync(Content entry, IDictionary<string, object> data, HttpContext httpContext, CancellationToken ct = default)
        => ApplyAsync(entry.ContentType, entry.Sensitivity, data, httpContext, ct);

    /// <summary>
    /// The write rule for an update of one stored entry. A field the caller may not read is put
    /// back to its stored value. A field the caller may read and the rule granting the update does
    /// not let them set is refused when the request changes it, and put back when it does not.
    /// </summary>
    /// <remarks>
    /// The default calls the overload that takes the stored data, which reverts what the caller
    /// may not see and refuses nothing.
    /// </remarks>
    ValueTask ApplyWriteAsync(Content existing, IDictionary<string, object> incoming, HttpContext httpContext, CancellationToken ct = default)
        => ApplyWriteAsync(existing.ContentType, incoming, existing.Data, httpContext, ct);

    /// <summary>
    /// Whether this caller reads a field of a content type: its sensitivity allows it and their
    /// permission rules show it. With an entry, for that entry; without one, for every entry of the
    /// type they may read.
    /// </summary>
    /// <remarks>
    /// What a read endpoint asks before it lets a caller filter or search on a field. The default is
    /// <see cref="MaySeeFieldAsync"/>, which knows no rule.
    /// </remarks>
    ValueTask<bool> MayReadFieldAsync(string contentType, FieldDefinition field, Content? entry, ClaimsPrincipal user, CancellationToken ct = default)
        => MaySeeFieldAsync(field, user, ct);

    /// <summary>
    /// Whether this caller reads the data of a document at the given sensitivity.
    /// </summary>
    /// <remarks>
    /// The document-level half of <see cref="MaySeeFieldAsync"/>: <see cref="ApplyAsync"/> clears the
    /// data of a document the caller may not see, and matching on that data would give it back one
    /// guess at a time. The default answers for a Public document only, with the same condition:
    /// an implementation that withholds a Public document has to override this.
    /// </remarks>
    ValueTask<bool> MaySeeDocumentAsync(SensitivityLevel level, ClaimsPrincipal user, CancellationToken ct = default)
        => ValueTask.FromResult(level == SensitivityLevel.Public);
}
