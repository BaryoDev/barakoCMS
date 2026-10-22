using System.Runtime.CompilerServices;
using barakoCMS.Core.Interfaces;
using Marten;

namespace barakoCMS.Infrastructure.Envelope;

/// <summary>The branch a write is for, and the node this store writes as.</summary>
/// <remarks>
/// The branch is ambient (an <see cref="AsyncLocal{T}"/>), so it reaches every session the request
/// opens, the scoped one and any a module opens straight from the store. The node is kept per store
/// rather than in a static, so two hosts in one process (the test fixtures) do not see each other's.
/// </remarks>
internal sealed class EventEnvelopeContext(IDocumentStore store) : IEventEnvelopeContext
{
    private static readonly AsyncLocal<Guid?> Ambient = new();
    private static readonly AsyncLocal<string?> AmbientCode = new();
    private static readonly ConditionalWeakTable<object, StrongBox<Guid>> Nodes = new();

    /// <summary>The configuration key that names this node. Unset means nothing is numbered.</summary>
    public const string NodeIdKey = "Barako:NodeId";

    /// <summary>The ambient branch, or null when the write is not for one.</summary>
    internal static Guid? CurrentBranch => Ambient.Value;

    /// <summary>Records the node a store writes as. Called once, while the store is configured.</summary>
    internal static void SetNode(StoreOptions options, Guid nodeId) => Nodes.AddOrUpdate(options, new StrongBox<Guid>(nodeId));

    /// <summary>The node a store writes as, or <see cref="Guid.Empty"/> when none was configured.</summary>
    internal static Guid NodeOf(IDocumentStore store) =>
        Nodes.TryGetValue(store.Options, out var box) ? box.Value : Guid.Empty;

    public Guid? BranchId => CurrentBranch;

    public string? BranchCode => AmbientCode.Value;

    public Guid NodeId => NodeOf(store);

    public IDisposable Use(Guid? branchId) => Use(branchId, null);

    public IDisposable Use(Guid? branchId, string? branchCode)
    {
        var previous = Ambient.Value;
        var previousCode = AmbientCode.Value;
        Ambient.Value = branchId;
        AmbientCode.Value = branchCode;
        return new Restore(previous, previousCode);
    }

    private sealed class Restore(Guid? previous, string? previousCode) : IDisposable
    {
        public void Dispose()
        {
            Ambient.Value = previous;
            AmbientCode.Value = previousCode;
        }
    }
}
