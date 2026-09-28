using barakoCMS.Infrastructure.Multitenancy;
using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace barakoCMS.Infrastructure.Services;

/// <summary>What a content batch decided: its result, and whether to commit what it wrote.</summary>
public readonly record struct ContentBatchOutcome<T>(T Result, bool Commit);

/// <summary>
/// Runs several content writes as one database transaction in which each write is visible to the
/// next.
/// </summary>
/// <remarks>
/// A lifecycle hook reads what is stored: a journal entry takes the next number from a stored
/// sequence, an account looks for its parent, a page walks its ancestors. Staged writes are not
/// stored, so in a batch that only stages, every entry is checked as if the ones before it did not
/// exist. Here each write is flushed through one open transaction, which the next entry's checks
/// read, and the transaction commits once at the end or rolls back, so the batch is still all or
/// nothing and a dry run can check everything and keep nothing.
///
/// The work runs in its own service scope, with the caller's tenant, whose sessions are all enlisted
/// in the transaction. Resolve what the batch uses from the provider it is handed, not from the
/// caller's scope, whose sessions are outside the transaction. A single create does not use this,
/// and behaves exactly as it did.
/// </remarks>
public interface IContentBatchRunner
{
    Task<T> RunAsync<T>(Func<IServiceProvider, CancellationToken, Task<ContentBatchOutcome<T>>> work, CancellationToken ct);
}

public sealed class ContentBatchRunner(
    IServiceScopeFactory scopes,
    IDocumentStore store,
    TenantContext tenant,
    IConfiguration configuration) : IContentBatchRunner
{
    public async Task<T> RunAsync<T>(
        Func<IServiceProvider, CancellationToken, Task<ContentBatchOutcome<T>>> work, CancellationToken ct)
    {
        await using var connection = store.Storage.Database.CreateConnection();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Marten sets the row-level security tenant on connections it opens, and this one it does
        // not open. Local to the transaction, so the pooled connection carries nothing afterwards.
        if (configuration.GetValue(DatabaseTenancy.EnabledKey, false))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "select set_config('app.tenant_id', @tenant, true)";
            command.Parameters.AddWithValue("tenant", tenant.IsDefault ? JasperFx.StorageConstants.DefaultTenantId : tenant.Slug);
            await command.ExecuteNonQueryAsync(ct);
        }

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Slug = tenant.Slug;
        scope.ServiceProvider.GetRequiredService<BatchTransaction>().Transaction = transaction;

        var outcome = await work(scope.ServiceProvider, ct);

        if (outcome.Commit)
            await transaction.CommitAsync(ct);
        else
            await transaction.RollbackAsync(ct);

        return outcome.Result;
    }
}
