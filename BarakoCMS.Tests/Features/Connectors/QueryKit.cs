using Marten;
using barakoCMS.Infrastructure.Connectors;
using barakoCMS.Models;

namespace BarakoCMS.Tests.Features.Connectors;

/// <summary>Seeds a content type and its entries into one tenant for the saved query tests.</summary>
internal static class QueryKit
{
    public static string Tenant() => "qk-" + Guid.NewGuid().ToString("n")[..10];

    public static string TypeName() => "qt" + Guid.NewGuid().ToString("n")[..8];

    public static FieldDefinition Field(string name, string type = "text", SensitivityLevel level = SensitivityLevel.Public) =>
        new() { Name = name, DisplayName = name, Type = type, Sensitivity = level };

    public static ContentTypeDefinition Type(string name, params FieldDefinition[] fields) => new()
    {
        Id = Guid.NewGuid(), Name = name, DisplayName = name, Fields = [.. fields],
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
    };

    public static Content Entry(
        string type, Dictionary<string, object> data, DateTime? createdAt = null,
        ContentStatus status = ContentStatus.Published, Guid? id = null, Guid? createdBy = null)
    {
        var at = createdAt ?? DateTime.UtcNow;
        return new Content
        {
            Id = id ?? Guid.NewGuid(), ContentType = type, Status = status, Data = data,
            CreatedAt = at, UpdatedAt = at, CreatedBy = createdBy ?? Guid.Empty, LastModifiedBy = createdBy ?? Guid.Empty,
        };
    }

    public static async Task SeedAsync(IDocumentStore store, string? tenant, ContentTypeDefinition type, IEnumerable<Content> entries, CancellationToken ct)
    {
        await using var session = tenant is null ? store.LightweightSession() : store.LightweightSession(tenant);
        session.Store(type);
        session.Store(entries.ToArray());
        await session.SaveChangesAsync(ct);
    }

    /// <summary>A role that reads the named types, and a user who holds it, in the default tenant. Returns the user's id.</summary>
    public static async Task<Guid> ActorAsync(IDocumentStore store, CancellationToken ct, params (string Type, string[]? Fields)[] reads)
    {
        var role = new Role
        {
            Id = Guid.NewGuid(),
            Name = "qr" + Guid.NewGuid().ToString("n")[..10],
            Permissions = [.. reads.Select(r => new ContentTypePermission
            {
                ContentTypeSlug = r.Type,
                Read = new PermissionRule { Enabled = true, ReadableFields = r.Fields?.ToList() },
            })],
        };
        var user = new User { Id = Guid.NewGuid(), Username = role.Name, Email = role.Name + "@example.com", RoleIds = [role.Id] };

        await using var session = store.LightweightSession();
        session.Store(role);
        session.Store(user);
        await session.SaveChangesAsync(ct);
        return user.Id;
    }

    /// <summary>The run a workflow step makes: parameters bound, reads checked as the user, or as nobody for an empty id.</summary>
    public static QueryRunContext AsActor(Guid actor, TimeSpan? timeout = null, params (string Name, string Value)[] values) =>
        new(Values(values)) { AsActor = true, Actor = actor, Timeout = timeout };

    /// <summary>The run a workflow step makes, which a runner offers through an interface only the runner's own assembly sees.</summary>
    public static Task<QueryResult> RunAsync(this QueryRunner runner, QueryDefinition definition, QueryRunContext context, CancellationToken ct) =>
        ((IContextQueryRunner)runner).RunAsync(definition, context, ct);

    public static QueryFilter Where(string field, string op, string value) => new() { Field = field, Op = op, Value = value };

    public static QueryFilter WhereParam(string field, string op, string param) => new() { Field = field, Op = op, Param = param };

    public static Dictionary<string, string> Values(params (string Name, string Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value);
}
