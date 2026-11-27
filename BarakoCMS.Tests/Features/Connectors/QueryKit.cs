using Marten;
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

    public static async Task SeedAsync(IDocumentStore store, string tenant, ContentTypeDefinition type, IEnumerable<Content> entries, CancellationToken ct)
    {
        await using var session = store.LightweightSession(tenant);
        session.Store(type);
        session.Store(entries.ToArray());
        await session.SaveChangesAsync(ct);
    }

    public static QueryFilter Where(string field, string op, string value) => new() { Field = field, Op = op, Value = value };

    public static QueryFilter WhereParam(string field, string op, string param) => new() { Field = field, Op = op, Param = param };

    public static Dictionary<string, string> Values(params (string Name, string Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value);
}
