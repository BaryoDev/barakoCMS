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
    bool Apply(string contentType, SensitivityLevel level, IDictionary<string, object> data, HttpContext httpContext);
}
