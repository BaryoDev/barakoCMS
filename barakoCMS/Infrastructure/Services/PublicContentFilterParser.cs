using barakoCMS.Core.Interfaces;
using barakoCMS.Features.Public;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// <see cref="IPublicContentFilterParser"/> over the parser and the SQL the core delivery routes use.
/// </summary>
/// <remarks>
/// Forwards to <c>DeliveryQuery</c>, so a module's route and <c>/api/public/{type}</c> accept the
/// same fields, the same operators and the same caps.
/// </remarks>
internal sealed class PublicContentFilterParser(IConfiguration config) : IPublicContentFilterParser
{
    public IPublicContentFilter Parse(IQueryCollection query, ContentTypeDefinition? definition)
    {
        ArgumentNullException.ThrowIfNull(query);

        return new Parsed(DeliveryQuery.Parse(
            DeliveryQuery.FilterPairs(query), definition, DeliveryQuery.MaxRadiusKm(config)));
    }

    private sealed class Parsed(DeliveryQuery parsed) : IPublicContentFilter
    {
        public string? Error => parsed.Error;

        public bool IsEmpty => parsed.Filters.Count == 0 && parsed.Near is null;

        public IQueryable<Content> Apply(IQueryable<Content> query)
        {
            if (!parsed.IsValid)
                throw new InvalidOperationException(
                    "The filters were refused. Answer the request with Error instead of running the query.");

            return parsed.ApplyTo(query);
        }
    }
}
