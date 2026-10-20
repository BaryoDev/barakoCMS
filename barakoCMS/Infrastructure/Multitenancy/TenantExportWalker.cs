using System.Linq.Expressions;
using System.Reflection;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using JasperFx.MultiTenancy;
using Marten;
using Marten.Schema;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>One document of a tenant's export: the section it belongs to, where it is kept, and its JSON.</summary>
/// <param name="Section">The document alias, or the section name a module contributor gave.</param>
/// <param name="Scope"><see cref="TenantExportScopes"/>: how the row is tied to the tenant.</param>
/// <param name="Json">The document as stored.</param>
internal sealed record TenantExportRow(string Section, string Scope, string Json);

internal static class TenantExportScopes
{
    /// <summary>A conjoined-tenancy document, carrying the tenant id.</summary>
    public const string Tenant = "tenant";

    /// <summary>A single-tenanted row keyed by the tenant's slug, such as a membership or an audit entry.</summary>
    public const string Global = "global";

    /// <summary>A row a module contributor supplied.</summary>
    public const string Module = "module";
}

/// <summary>
/// Walks every document that carries a tenant: the conjoined documents Marten partitions by tenant
/// id, and the single-tenanted rows that name the tenant in a <c>TenantSlug</c> property. It is the
/// part export, import and erase share, so none of them keeps its own list of tables.
/// </summary>
/// <remarks>
/// The list comes from the store's document mappings, not from a hand-kept list, so a document
/// added later is walked without anyone remembering it. Rows marked deleted by a
/// <c>DeletedAt</c> property are ordinary rows to Marten and are included, since a tenant erase that
/// skipped them would leave rows carrying the tenant behind.
///
/// Three document types are withheld rather than exported: connector secrets and the mail provider
/// settings hold ciphertext under this deployment's key, which a target cannot open, and a pending
/// registration holds a verification token. Connector secrets will travel re-wrapped under a
/// transfer key in a later slice, and never as they are stored.
/// </remarks>
internal sealed class TenantExportWalker
{
    private const string SlugProperty = "TenantSlug";

    private static readonly MethodInfo StreamMethod =
        typeof(TenantExportWalker).GetMethod(nameof(StreamAsync), BindingFlags.Static | BindingFlags.NonPublic)!;

    /// <summary>Types that hold a secret or a one-time token and are never written to an export as stored.</summary>
    public static readonly IReadOnlyList<Type> Withheld = [typeof(ConnectorSecret), typeof(EmailSettings), typeof(PendingRegistration)];

    private readonly IDocumentStore _store;
    private readonly IReadOnlyList<ITenantDataContributor> _contributors;

    public TenantExportWalker(IDocumentStore store, IEnumerable<ITenantDataContributor> contributors)
    {
        _store = store;
        _contributors = contributors.ToList();
    }

    /// <summary>Every document type the store knows.</summary>
    public IReadOnlyList<IDocumentType> Mappings() => _store.Options.AllKnownDocumentTypes();

    /// <summary>The document types the walk reads for a tenant, with how each is tied to it.</summary>
    public IReadOnlyList<(Type Type, string Alias, string Scope)> Plan()
    {
        var plan = new List<(Type, string, string)>();
        foreach (var mapping in Mappings().OrderBy(m => m.Alias, StringComparer.Ordinal))
        {
            var type = mapping.DocumentType;
            if (Withheld.Contains(type))
                continue;

            if (mapping.TenancyStyle == TenancyStyle.Conjoined)
                plan.Add((type, mapping.Alias, TenantExportScopes.Tenant));
            else if (type == typeof(Tenant) || SlugPropertyOf(type) is not null)
                plan.Add((type, mapping.Alias, TenantExportScopes.Global));
        }

        return plan;
    }

    /// <summary>Every row carrying <paramref name="slug"/>, document types first, then module contributions.</summary>
    public async IAsyncEnumerable<TenantExportRow> WalkAsync(
        string slug, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var serializer = _store.Options.Serializer();
        await using var session = slug == Tenant.DefaultSlug ? _store.QuerySession() : _store.QuerySession(slug);

        foreach (var (type, alias, scope) in Plan())
        {
            var rows = (IAsyncEnumerable<object>)StreamMethod.MakeGenericMethod(type)
                .Invoke(null, [session, scope, slug, ct])!;

            await foreach (var document in rows.WithCancellation(ct))
                yield return new TenantExportRow(alias, scope, serializer.ToJson(document));
        }

        foreach (var contributor in _contributors)
        {
            await foreach (var json in contributor.ExportAsync(slug, ct).WithCancellation(ct))
                yield return new TenantExportRow(contributor.Section, TenantExportScopes.Module, json);
        }
    }

    private static PropertyInfo? SlugPropertyOf(Type type)
    {
        var property = type.GetProperty(SlugProperty, BindingFlags.Public | BindingFlags.Instance);
        return property?.PropertyType == typeof(string) ? property : null;
    }

    private static async IAsyncEnumerable<object> StreamAsync<T>(
        IQuerySession session, string scope, string slug,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) where T : class
    {
        IQueryable<T> query = session.Query<T>();
        if (scope == TenantExportScopes.Global)
            query = query.Where(SlugFilter<T>(slug));

        await foreach (var document in query.ToAsyncEnumerable(ct))
            yield return document;
    }

    /// <summary>The registry row matches on its <c>Slug</c>, every other global row on <c>TenantSlug</c>.</summary>
    private static Expression<Func<T, bool>> SlugFilter<T>(string slug)
    {
        var parameter = Expression.Parameter(typeof(T), "x");
        var member = typeof(T) == typeof(Tenant) ? nameof(Tenant.Slug) : SlugProperty;
        var body = Expression.Equal(Expression.Property(parameter, member), Expression.Constant(slug, typeof(string)));
        return Expression.Lambda<Func<T, bool>>(body, parameter);
    }
}
