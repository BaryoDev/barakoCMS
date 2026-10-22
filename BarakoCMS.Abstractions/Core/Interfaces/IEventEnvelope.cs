namespace barakoCMS.Core.Interfaces;

/// <summary>The header names stamped on every content event so stations can sync and merge.</summary>
public static class EnvelopeHeaders
{
    public const string BranchId = "branch-id";
    public const string OriginNodeId = "origin-node-id";
    public const string OriginSequence = "origin-sequence";
}

/// <summary>The branch of the current request and the id of this node, read when events are stamped.</summary>
public interface IEventEnvelopeContext
{
    Guid? BranchId { get; }

    /// <summary>The code of <see cref="BranchId"/>, or null when there is no branch or it has no code.</summary>
    string? BranchCode { get; }

    Guid NodeId { get; }
    IDisposable Use(Guid? branchId);

    /// <summary>Sets the branch and its code, for numbering that carries the branch in the series name.</summary>
    IDisposable Use(Guid? branchId, string? branchCode);
}

/// <summary>Reserves the next gapless origin sequence number of a node inside the caller's transaction.</summary>
public interface IOriginSequence
{
    Task<long> ReserveAsync(Marten.IDocumentSession session, Guid nodeId, CancellationToken ct);
}
