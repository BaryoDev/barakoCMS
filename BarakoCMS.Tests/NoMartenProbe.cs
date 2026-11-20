using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using barakoCMS.Modules;

namespace BarakoCMS.Tests;

// This file is the point of #948: a module that seeds, reads content and guards a write using
// barakoCMS types only. It must never name the persistence library, and
// The_probe_names_no_marten_type checks that it has not.

internal sealed class NoMartenProbeModule(string typeName, string roleName, Guid roleId) : IBarakoModule
{
    public string Name => "NoMartenProbe";

    public int SeenWhileSeeding { get; private set; }

    public bool ContentTypeAdded { get; private set; }

    public bool RoleAdded { get; private set; }

    public async Task SeedAsync(IModuleSeedContext context, CancellationToken ct)
    {
        SeenWhileSeeding = await context.Reader.CountAsync(typeName, ct);
        ContentTypeAdded = await context.EnsureContentTypeAsync(
            new ContentTypeDefinition { Id = Guid.NewGuid(), Name = typeName, DisplayName = typeName }, ct);
        RoleAdded = await context.EnsureRoleAsync(roleId, roleName, "Made by the probe module.", ct);
    }
}

internal sealed class NoMartenProbeHook(string typeName) : IContentLifecycleHook
{
    public string ContentType => typeName;

    public async Task<IReadOnlyList<string>> BeforeSaveAsync(IContentHookContext context, CancellationToken ct)
    {
        var code = context.Data.TryGetValue("Code", out var value) ? value?.ToString() : null;
        context.Data["Checked"] = true;

        var errors = new List<string>();
        await foreach (var other in context.Reader.ReadAllAsync(typeName, ct))
        {
            var otherCode = other.Data.TryGetValue("Code", out var o) ? o?.ToString() : null;
            if (other.Id != context.EntryId && string.Equals(otherCode, code, StringComparison.Ordinal))
            {
                errors.Add($"Code '{code}' is already in use.");
            }
        }

        return errors;
    }
}

internal sealed class NoMartenLockHook(string typeName, string key) : IContentLifecycleHook
{
    public string ContentType => typeName;

    public async Task<IReadOnlyList<string>> BeforeSaveAsync(IContentHookContext context, CancellationToken ct)
    {
        await context.LockAsync(key, ct);
        return [];
    }
}

internal sealed class NoMartenDocumentHook(string typeName, Guid roleId, bool store) : IContentLifecycleHook
{
    public string ContentType => typeName;

    public ProbeDocument? Loaded { get; private set; }

    public async Task<IReadOnlyList<string>> BeforeSaveAsync(IContentHookContext context, CancellationToken ct)
    {
        if (store)
        {
            context.Store(new ProbeDocument { Id = roleId, Name = "stored-by-hook" });
        }

        Loaded = await context.LoadAsync<ProbeDocument>(roleId, ct);
        return [];
    }
}

/// <summary>A document a module owns, as a hook stores it through its context.</summary>
public sealed class ProbeDocument
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Count { get; set; }
}

/// <summary>Loads a counter document, adds one and stores it back, as a numbering hook does.</summary>
internal sealed class NoMartenCounterHook(string typeName, Guid counterId) : IContentLifecycleHook
{
    public string ContentType => typeName;

    public async Task<IReadOnlyList<string>> BeforeSaveAsync(IContentHookContext context, CancellationToken ct)
    {
        var counter = await context.LoadAsync<ProbeDocument>(counterId, ct)
            ?? new ProbeDocument { Id = counterId, Name = "counter" };
        counter.Count++;
        context.Store(counter);
        return [];
    }
}

/// <summary>Stores a document, then refuses the write, so the staged document must not survive.</summary>
internal sealed class NoMartenStoreThenRefuseHook(string typeName, Guid documentId, string refuseCode) : IContentLifecycleHook
{
    public string ContentType => typeName;

    public Task<IReadOnlyList<string>> BeforeSaveAsync(IContentHookContext context, CancellationToken ct)
    {
        if (context.Data.TryGetValue("Code", out var code) && code?.ToString() == refuseCode)
        {
            context.Store(new ProbeDocument { Id = documentId, Name = "staged-by-refused-row" });
            return Task.FromResult<IReadOnlyList<string>>(["refused"]);
        }

        return Task.FromResult<IReadOnlyList<string>>([]);
    }
}

/// <summary>Reaches for a core document through the context.</summary>
internal sealed class NoMartenCoreDocumentHook(string typeName) : IContentLifecycleHook
{
    public string ContentType => typeName;

    public async Task<IReadOnlyList<string>> BeforeSaveAsync(IContentHookContext context, CancellationToken ct)
    {
        await context.LoadAsync<Role>(Guid.NewGuid(), ct);
        return [];
    }
}
