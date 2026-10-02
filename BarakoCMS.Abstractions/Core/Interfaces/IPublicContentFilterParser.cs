using barakoCMS.Models;
using Microsoft.AspNetCore.Http;

namespace barakoCMS.Core.Interfaces;

/// <summary>
/// The <c>filter[field][op]=value</c> parameters of one request, checked against a content type.
/// </summary>
public interface IPublicContentFilter
{
    /// <summary>
    /// Why the request is refused, or null when every filter was accepted. Answer 400 with it.
    /// </summary>
    string? Error { get; }

    /// <summary>True when the request carried no filter, so <see cref="Apply"/> changes nothing.</summary>
    bool IsEmpty { get; }

    /// <summary>
    /// Narrows a content query to the entries that match every filter.
    /// </summary>
    /// <remarks>
    /// The filters are added to the query passed in. They do not select Published or
    /// document-Public entries: the caller's query has to, the same as without a filter. Throws
    /// when <see cref="Error"/> is set, since a refused filter must not run as no filter.
    /// </remarks>
    IQueryable<Content> Apply(IQueryable<Content> query);
}

/// <summary>
/// Reads a request's field filters the way <c>GET /api/public/{type}</c> does, for a module that
/// serves its own anonymous route over content.
/// </summary>
/// <remarks>
/// A filter on a field the caller cannot read tells the caller its value by which entries come
/// back, so only fields the content type marks Public are accepted, and anything else is refused
/// instead of ignored. A module that parsed the parameters itself would have to hold a second copy
/// of that rule and of the SQL behind it. Field names are checked against the type's declared
/// fields and values travel as bound parameters.
///
/// <c>sort</c> is not read here. Only keys that start with <c>filter[</c> are.
/// </remarks>
public interface IPublicContentFilterParser
{
    /// <summary>
    /// Parses the filters in <paramref name="query"/> against <paramref name="definition"/>.
    /// </summary>
    /// <param name="query">The request's query string.</param>
    /// <param name="definition">
    /// The content type the route serves. Null is refused, the same as a type with no Public fields.
    /// </param>
    IPublicContentFilter Parse(IQueryCollection query, ContentTypeDefinition? definition);
}
