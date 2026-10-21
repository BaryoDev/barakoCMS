using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Rules;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Tests.Rules;

/// <summary>Stores a content type with rules and runs the engine against it.</summary>
internal static class RuleKit
{
    public static RuleCondition Cond(string field, string op, string? value = null, string kind = "field", bool negate = false) =>
        new() { Kind = kind, Field = field, Op = op, Value = value, Negate = negate };

    public static RuleDefinition Rule(string name, params RuleCondition[] all) =>
        new() { Name = name, All = [.. all], Message = $"{name} failed" };

    public static async Task<string> TypeAsync(IntegrationTestFixture host, params RuleDefinition[] rules)
    {
        var name = $"rule{Guid.NewGuid():n}"[..20];
        using var scope = host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        session.Store(new ContentTypeDefinition { Id = Guid.NewGuid(), Name = name, Rules = [.. rules] });
        await session.SaveChangesAsync();
        return name;
    }

    public static async Task<RuleResult> RunAsync(
        IntegrationTestFixture host, string type, IDictionary<string, object?> data,
        RuleWhen when = RuleWhen.Save, string? transition = null, string? from = null, Content? existing = null,
        IEnumerable<IRuleLookup>? lookups = null, TimeProvider? clock = null)
    {
        using var scope = host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var ctx = new RuleContext(type, existing?.Id, data, existing, transition, from, null, Guid.NewGuid(), session);
        return await new RuleEngine(lookups ?? [], clock).EvaluateAsync(when, ctx, TestContext.Current.CancellationToken);
    }

    public static Dictionary<string, object?> Data(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Rows stored straight into the document table, for rules that read other entries.</summary>
internal static class RuleRows
{
    public static async Task<Guid> StoreAsync(
        IntegrationTestFixture host, string type, Dictionary<string, object> data, string? state = null, Guid? branchId = null)
    {
        using var scope = host.Services.CreateScope();
        var session = scope.ServiceProvider.GetRequiredService<IDocumentSession>();
        var row = new Content
        {
            Id = Guid.NewGuid(), ContentType = type, Data = data, LifecycleState = state, Status = ContentStatus.Draft, BranchId = branchId,
        };
        session.Store(row);
        await session.SaveChangesAsync();
        return row.Id;
    }
}

/// <summary>A clock fixed at one instant.</summary>
internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
