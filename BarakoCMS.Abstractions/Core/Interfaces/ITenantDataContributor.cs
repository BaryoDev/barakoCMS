namespace barakoCMS.Core.Interfaces;

/// <summary>
/// Lets a module put its own data into a tenant export, for what the core cannot see: a document
/// the module keeps outside Marten, or bytes such as stored files.
/// </summary>
/// <remarks>
/// Documents a module registers with Marten are found by the export without this. Register an
/// implementation as a singleton or scoped service. It must read only the tenant it is given, and
/// must not return a secret in clear.
/// </remarks>
public interface ITenantDataContributor
{
    /// <summary>The section name the rows are filed under in the export. Unique across contributors.</summary>
    string Section { get; }

    /// <summary>One JSON document per item the module holds for <paramref name="tenantSlug"/>.</summary>
    IAsyncEnumerable<string> ExportAsync(string tenantSlug, CancellationToken ct);
}
