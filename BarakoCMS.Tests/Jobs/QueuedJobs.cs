using barakoCMS.Infrastructure.Jobs;
using barakoCMS.Models;
using Marten;

namespace BarakoCMS.Tests.Jobs;

/// <summary>Reads the job table the way a test needs: across tenants, or inside one.</summary>
internal static class QueuedJobs
{
    /// <summary>The jobs whose command names <paramref name="changeId"/>, in any tenant.</summary>
    public static async Task<List<JobRecord>> ForChangeAsync(IDocumentStore store, Guid changeId, CancellationToken ct)
    {
        var id = changeId.ToString();
        await using var query = store.QuerySession();
        return (await query.Query<JobRecord>()
            .Where(r => r.AnyTenant() && r.CommandJson.Contains(id))
            .ToListAsync(ct)).ToList();
    }

    /// <summary>The field change jobs stored in <paramref name="tenant"/>.</summary>
    public static async Task<int> FieldChangeCountAsync(IDocumentStore store, string tenant, CancellationToken ct)
    {
        var type = typeof(FieldChangeCommand).FullName;
        await using var query = store.QuerySession(tenant);
        return await query.Query<JobRecord>().CountAsync(r => r.CommandType == type, ct);
    }
}
