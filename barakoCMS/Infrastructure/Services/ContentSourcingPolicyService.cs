using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <inheritdoc />
/// <remarks>
/// Public for the same reason <see cref="ContentWriter"/> is: a module that constructs its own
/// writer against its own session has to be able to build one of these too, or its writes would take
/// the document path whatever the type's policy says.
/// </remarks>
public sealed class ContentSourcingPolicyService : IContentSourcingPolicy
{
    private readonly IDocumentSession _session;

    /// <summary>Policies already resolved in this request.</summary>
    /// <remarks>
    /// A single content write asks the same question up to three times (the writer on append, the
    /// endpoint for the concurrency rule, the endpoint again for the status code), and the answer
    /// cannot change inside one request: nothing mutates a policy. Caching it keeps a routing
    /// decision from costing a query per call site.
    /// </remarks>
    private readonly Dictionary<string, ContentTypeSourcingPolicy?> _resolved =
        new(StringComparer.OrdinalIgnoreCase);

    public ContentSourcingPolicyService(IDocumentSession session) => _session = session;

    /// <inheritdoc />
    public async Task<ContentTypeSourcingPolicy?> GetAsync(string contentTypeName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(contentTypeName))
        {
            return null;
        }

        var key = barakoCMS.Core.ContentTypeName.Normalize(contentTypeName);
        if (_resolved.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var policy = await _session.LoadAsync<ContentTypeSourcingPolicy>(key, cancellationToken);
        _resolved[key] = policy;
        return policy;
    }

    /// <inheritdoc />
    public async Task<bool> IsEventSourcedAsync(string contentTypeName, CancellationToken cancellationToken)
        => (await GetAsync(contentTypeName, cancellationToken))?.EventSourced ?? false;

    /// <inheritdoc />
    public async Task<ContentTypeSourcingPolicy> DecideAsync(string contentTypeName, bool eventSourced, CancellationToken cancellationToken)
    {
        var key = barakoCMS.Core.ContentTypeName.Normalize(contentTypeName);

        var standing = await GetAsync(key, cancellationToken);
        if (standing is not null)
        {
            return standing;
        }

        var policy = new ContentTypeSourcingPolicy
        {
            Name = key,
            EventSourced = eventSourced,
            DecidedAt = DateTimeOffset.UtcNow,
        };

        _session.Store(policy);
        _resolved[key] = policy;
        return policy;
    }
}
