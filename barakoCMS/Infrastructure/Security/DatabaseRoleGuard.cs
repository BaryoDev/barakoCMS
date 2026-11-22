using System.Data.Common;
using System.Runtime.CompilerServices;
using Marten;

namespace barakoCMS.Infrastructure.Security;

/// <summary>
/// Refuses to start outside Development when the database role the API connects as is a superuser,
/// has BYPASSRLS, or belongs to the admin role a managed Postgres service gives its owner.
/// </summary>
/// <remarks>
/// <para>
/// A superuser, or any role with BYPASSRLS, ignores every row level security policy, and it can
/// read and change anything in the database whatever the API's own checks say. The API needs none
/// of that. <c>scripts/db-app-role.sql</c> creates a role that owns what the API uses and has none
/// of it, and the compose files and the Kubernetes manifests connect as that role.
/// </para>
/// <para>
/// What counts: <c>rolsuper</c> or <c>rolbypassrls</c> for <c>current_user</c>, or membership in
/// <c>rds_superuser</c> (Amazon RDS and Aurora), <c>azure_pg_admin</c> (Azure Database for
/// PostgreSQL) or <c>cloudsqlsuperuser</c> (Google Cloud SQL), whichever of them exist on the
/// server. Those services keep a real superuser to themselves and give their admin user
/// <c>rolsuper = false</c> with one of these memberships, so the role attributes alone would pass
/// the account that holds the most power the service hands out. A role the query cannot read
/// refuses too.
/// </para>
/// <para>
/// <see cref="AllowKey"/> turns the check off for a deployment that has decided to run this way.
/// Only <c>true</c>, in any letter case, allows it. The check reads fixed SQL over the store's own
/// connection and never prints the connection string or a password.
/// </para>
/// <para>
/// This guard applies in every environment but Development, and is separate from
/// <c>DatabaseTenancy.AssertUsableAsync</c>, which refuses a superuser in every environment
/// while <c>Tenancy:DatabaseEnforcement</c> is on.
/// </para>
/// </remarks>
internal static class DatabaseRoleGuard
{
    /// <summary>The setting that lets a deployment run as a superuser. False unless exactly <c>true</c>.</summary>
    public const string AllowKey = "Database:AllowSuperuser";

    /// <summary>The roles a managed Postgres service gives its admin user, in place of rolsuper.</summary>
    internal static readonly string[] ManagedAdminRoles = ["rds_superuser", "azure_pg_admin", "cloudsqlsuperuser"];

    /// <summary>
    /// The role's name, its two attributes and the first managed admin role it belongs to. A missing
    /// admin role on the server is skipped by <c>to_regrole</c> rather than raising an error.
    /// </summary>
    internal const string RoleQuery =
        """
        select r.rolname::text,
               r.rolsuper,
               r.rolbypassrls,
               (select a.name
                  from unnest(array['rds_superuser', 'azure_pg_admin', 'cloudsqlsuperuser']::text[]) as a(name)
                 where to_regrole(a.name) is not null
                   and pg_has_role(current_user, to_regrole(a.name)::oid, 'member')
                 order by a.name
                 limit 1)
          from pg_roles r
         where r.rolname = current_user
        """;

    /// <summary>One warning per start, even where the schema apply runs twice in a process.</summary>
    private static readonly ConditionalWeakTable<object, StrongBox<int>> Warned = new();

    internal sealed record RoleFacts(string Name, bool Superuser, bool BypassRls, string? AdminRole);

    /// <summary>Whether the key allows a superuser, and whether its value was one the guard knows.</summary>
    internal static (bool Allowed, bool Recognised) ReadAllow(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (false, true);
        }

        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
        {
            return (true, true);
        }

        return (false, string.Equals(value, "false", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Runs the check over the store's connection. <paramref name="startKey"/> identifies this start,
    /// so the warning for an allowed superuser is logged once for it. The connect is the store's own,
    /// the one the schema apply makes next, so a database that is not up yet fails here as it would
    /// there.
    /// </summary>
    /// <exception cref="InvalidOperationException">The role is privileged or cannot be read, and the key does not allow it.</exception>
    public static Task AssertAsync(
        IConfiguration configuration,
        bool isDevelopment,
        IDocumentStore store,
        ILogger logger,
        object startKey,
        CancellationToken ct = default) =>
        AssertAsync(
            configuration,
            isDevelopment,
            async token =>
            {
                var connection = store.Storage.Database.CreateConnection();
                await connection.OpenAsync(token);
                return connection;
            },
            logger,
            startKey,
            RoleQuery,
            ct);

    internal static async Task AssertAsync(
        IConfiguration configuration,
        bool isDevelopment,
        Func<CancellationToken, Task<DbConnection>> open,
        ILogger logger,
        object startKey,
        string query,
        CancellationToken ct = default)
    {
        if (isDevelopment)
        {
            return;
        }

        var (allowed, recognised) = ReadAllow(configuration[AllowKey]);
        if (allowed)
        {
            if (Interlocked.Exchange(ref Warned.GetOrCreateValue(startKey).Value, 1) == 0)
            {
                logger.LogWarning(
                    "{Key} is true, so the API does not check whether its database role is a superuser or bypasses row level security.",
                    AllowKey);
            }

            return;
        }

        await using var connection = await open(ct);

        RoleFacts? facts;
        try
        {
            facts = await ReadAsync(connection, query, ct);
        }
        catch (DbException ex)
        {
            // The exception's own text can carry server details, so only its type is kept.
            throw new InvalidOperationException(Refusal(null, recognised, ex.GetType().Name));
        }

        var reason = facts is null ? null : Reason(facts);
        if (facts is not null && reason is null)
        {
            return;
        }

        throw new InvalidOperationException(Refusal(facts is null ? null : (facts.Name, reason!), recognised, null));
    }

    private static async Task<RoleFacts?> ReadAsync(DbConnection connection, string query, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = query;

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)
            || reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2))
        {
            return null;
        }

        return new RoleFacts(
            reader.GetString(0),
            reader.GetBoolean(1),
            reader.GetBoolean(2),
            reader.FieldCount > 3 && !reader.IsDBNull(3) ? reader.GetString(3) : null);
    }

    /// <summary>What makes the role privileged, or null when nothing does.</summary>
    private static string? Reason(RoleFacts facts)
    {
        var parts = new List<string>();
        if (facts.Superuser)
        {
            parts.Add("is a superuser");
        }

        if (facts.BypassRls)
        {
            parts.Add("has BYPASSRLS");
        }

        if (facts.AdminRole is not null)
        {
            parts.Add($"is a member of '{facts.AdminRole}', the admin role of a managed Postgres service");
        }

        return parts.Count == 0 ? null : string.Join(" and ", parts);
    }

    private static string Refusal((string Name, string Reason)? role, bool recognised, string? errorType)
    {
        var subject = role is { } known
            ? $"The API connects to Postgres as '{known.Name}', which {known.Reason}."
            : "The API could not read the attributes of the database role it connects as"
              + (errorType is null ? "." : $" ({errorType}).")
              + " It cannot tell whether that role is a superuser.";

        return subject
            + " Outside Development the API refuses to start as a role that bypasses row level security. "
            + "Connect as a role without superuser: scripts/db-app-role.sql creates one. "
            + $"A deployment that has decided to run this way sets {AllowKey}=true "
            + $"(environment variable {AllowKey.Replace(":", "__")})."
            + (recognised
                ? string.Empty
                : $" The value of {AllowKey} is not recognised: only true allows it, in any letter case.");
    }
}
