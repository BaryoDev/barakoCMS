using Marten;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Infrastructure.Multitenancy;

/// <summary>
/// Whether Postgres is enforcing the tenant filter, and whether this deployment is actually set up
/// for it to.
/// </summary>
/// <remarks>
/// Marten generates the policies (`ENABLE`, `FORCE`, and a `USING`/`WITH CHECK` pair on
/// `app.tenant_id`) for every conjoined document table when
/// <c>UseRowLevelSecurity</c> is on. That part is a line of configuration.
///
/// The part that is not is who the application connects as. **A Postgres superuser bypasses row
/// level security completely**, whatever is on the table, and every deployment this repository
/// ships connects as `postgres`. Measured, not assumed: on the same table with the same policy, a
/// superuser sees every tenant's rows and a `NOSUPERUSER` owner sees one tenant's.
///
/// So the flag on its own would put policies on every table, have them show up in `pg_policies`,
/// satisfy any check that asks whether row level security is enabled, and enforce nothing at all.
/// A security control that looks configured and is inert is worse than not having it, because the
/// next person reads the policy and stops worrying.
///
/// That is why this refuses to start. The alternative is a log line nobody reads on a deployment
/// that believes it is protected.
/// </remarks>
public static class DatabaseTenancy
{
    /// <summary>Turns the policies on. False by default, and by omission.</summary>
    public const string EnabledKey = "Tenancy:DatabaseEnforcement";

    /// <summary>
    /// Refuses to start when enforcement is on and the connection cannot be subject to it.
    /// </summary>
    /// <remarks>
    /// Only the superuser check, deliberately. Being the table owner is fine: Marten emits
    /// <c>FORCE ROW LEVEL SECURITY</c>, which binds the owner, and that was measured rather than
    /// read. Superuser is the one attribute Postgres will not let a policy bind.
    /// </remarks>
    public static async Task AssertUsableAsync(
        IConfiguration configuration, IDocumentStore store, ILogger logger, CancellationToken ct = default)
    {
        if (!configuration.GetValue(EnabledKey, false))
        {
            return;
        }

        await using var connection = store.Storage.Database.CreateConnection();
        await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = "select current_user, (select rolsuper from pg_roles where rolname = current_user)";

        await using var reader = await command.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            throw new InvalidOperationException(
                $"{EnabledKey} is on and this could not determine which role the application connects as. "
              + "Refusing to start, because enforcement that cannot be verified is enforcement nobody should rely on.");
        }

        var role = reader.GetString(0);
        var isSuperuser = !reader.IsDBNull(1) && reader.GetBoolean(1);

        if (isSuperuser)
        {
            throw new InvalidOperationException(
                $"{EnabledKey} is on, but this application connects to Postgres as '{role}', which is a "
              + "superuser. A superuser bypasses row level security entirely, so the tenant policies "
              + "would be applied to every table and enforce nothing. This refuses to start rather "
              + "than run while appearing to be protected.\n\n"
              + "Connect as a role created with NOSUPERUSER, or turn the setting off. "
              + "docs/tenancy-at-the-database.md has the role, the ownership transfer and the "
              + "connection string.");
        }

        logger.LogInformation(
            "Tenant isolation is enforced by Postgres. Connected as {Role}, which is not a superuser.", role);
    }
}
