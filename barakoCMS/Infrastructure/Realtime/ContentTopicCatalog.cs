using System.Collections.Concurrent;
using barakoCMS.Infrastructure.Multitenancy;
using barakoCMS.Models;
using Marten;
using Marten.Services;

namespace barakoCMS.Infrastructure.Realtime;

/// <summary>A realtime topic a content type declared, and whether its entries belong to one branch each.</summary>
public sealed record ContentTopic(string Name, string ContentType, bool BranchScoped);

/// <summary>
/// The realtime topics content types declare, per tenant, read once and kept until a content type
/// of that tenant is written.
/// </summary>
/// <remarks>
/// Registered as a Marten listener, so a type saved with a new, changed or removed topic is seen by
/// the next subscribe or notice without a restart. The listener drops only the tenant that wrote.
/// Only connections on the writing instance see the drop at once; another instance reads the new
/// topics the next time its cache is empty, which is after a restart.
///
/// The store comes as a function because the catalog is added to the store's own listeners, and the
/// store cannot be asked for while it is being built.
/// </remarks>
public sealed class ContentTopicCatalog(Func<IDocumentStore> store) : DocumentSessionListenerBase
{
    private readonly ConcurrentDictionary<string, Task<Dictionary<string, ContentTopic>>> _byTenant = new();

    public ContentTopicCatalog(IDocumentStore store) : this(() => store)
    {
    }

    public async Task<ContentTopic?> FindAsync(string tenantSlug, string topic, CancellationToken ct)
    {
        var topics = await LoadAsync(tenantSlug, ct);
        return topics.GetValueOrDefault(topic);
    }

    public async Task<ContentTopic?> ForTypeAsync(string tenantSlug, string contentType, CancellationToken ct)
    {
        var topics = await LoadAsync(tenantSlug, ct);
        return topics.Values.FirstOrDefault(t => string.Equals(t.ContentType, contentType, StringComparison.OrdinalIgnoreCase));
    }

    public override Task AfterCommitAsync(IDocumentSession session, IChangeSet commit, CancellationToken token)
    {
        var touched = commit.Inserted.Concat(commit.Updated).OfType<ContentTypeDefinition>().Any()
            || commit.Deleted.Any(d => d.DocumentType == typeof(ContentTypeDefinition));
        if (touched) _byTenant.TryRemove(TenantScopes.SlugFor(session.TenantId), out _);
        return Task.CompletedTask;
    }

    // The caller's token is not passed on: the first caller's cancellation would otherwise be kept
    // for everyone who asks while the task is cached.
    private Task<Dictionary<string, ContentTopic>> LoadAsync(string slug, CancellationToken ct) =>
        _byTenant.GetOrAdd(slug, key => ReadKeepingNoFailureAsync(key)).WaitAsync(ct);

    private async Task<Dictionary<string, ContentTopic>> ReadKeepingNoFailureAsync(string slug)
    {
        try
        {
            return await ReadAsync(slug);
        }
        catch
        {
            _byTenant.TryRemove(slug, out _);
            throw;
        }
    }

    private async Task<Dictionary<string, ContentTopic>> ReadAsync(string slug)
    {
        await using var read = store().QuerySession(TenantScopes.TenantIdFor(slug));
        var types = await read.Query<ContentTypeDefinition>().Where(d => d.RealtimeTopic != null).ToListAsync();
        return types.ToDictionary(d => d.RealtimeTopic!, d => new ContentTopic(d.RealtimeTopic!, d.Name, d.BranchScoped), StringComparer.Ordinal);
    }
}
