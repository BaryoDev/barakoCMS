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
