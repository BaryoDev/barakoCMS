using System.Security.Claims;
using System.Text.Json;

namespace barakoCMS.Core.Interfaces;

/// <summary>Who an export, plan or apply runs for.</summary>
/// <remarks>
/// <see cref="System"/> is the host itself, applying a file it was configured with at startup. It is
/// not a caller with every grant: a provider that needs a signed-in caller refuses it.
/// </remarks>
public sealed class ConfigCaller
{
    private ConfigCaller(ClaimsPrincipal? principal) => Principal = principal;

    /// <summary>The host applying a file it was configured with, with no request behind it.</summary>
    public static ConfigCaller System { get; } = new(null);

    /// <summary>A signed-in caller.</summary>
    public static ConfigCaller ForUser(ClaimsPrincipal principal) =>
        new(principal ?? throw new ArgumentNullException(nameof(principal)));

    /// <summary>The signed-in caller, or null for <see cref="System"/>.</summary>
    public ClaimsPrincipal? Principal { get; }

    /// <summary>Whether this is <see cref="System"/>.</summary>
    public bool IsSystem => Principal is null;
}

/// <summary>One section as exported.</summary>
public sealed class ConfigSectionExport
{
    /// <summary>The documents, by key.</summary>
    public required IReadOnlyDictionary<string, JsonElement> Documents { get; init; }

    /// <summary>
    /// What is stored and could not be exported, each said in a sentence, so a promotion does not
    /// lose configuration without anybody being told.
    /// </summary>
    public IReadOnlyList<string> Gaps { get; init; } = [];
}

/// <summary>What one section of a document would do.</summary>
public sealed class ConfigSectionPlan
{
    /// <summary>The section planned.</summary>
    public required string Section { get; init; }

    /// <summary>Each document that would change, in key order.</summary>
    public IReadOnlyList<ConfigChange> Changes { get; init; } = [];

    /// <summary>Why the section cannot be applied as written. Answered as a 400.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>What this caller may not do that the section asks for. Answered as a 403.</summary>
    public IReadOnlyList<string> Refusals { get; init; } = [];

    /// <summary>What is worth knowing and changes nothing, such as a secret still to be set.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>One document that would change.</summary>
public sealed class ConfigChange
{
    /// <summary>The document's key in the section.</summary>
    public required string Key { get; init; }

    /// <summary>What kind of change this is.</summary>
    public required ConfigChangeKind Kind { get; init; }

    /// <summary>The change in one line, for a person reviewing the plan.</summary>
    public required string Summary { get; init; }

    /// <summary>The parts of the document that change, each in a line.</summary>
    public IReadOnlyList<string> Details { get; init; } = [];

    /// <summary>The document as the section declares it, or null when the change removes it.</summary>
    public JsonElement? Desired { get; init; }
}

/// <summary>What a change does to what is stored.</summary>
/// <remarks>
/// <see cref="Destructive"/> is a removal, or a change that loses something stored: a field and the
/// values held in it, a credential, a document. Apply refuses a plan holding one unless the caller
/// says it may.
/// </remarks>
public enum ConfigChangeKind
{
    /// <summary>Not set. Never planned.</summary>
    Unknown = 0,

    /// <summary>A document that is not stored yet.</summary>
    Create = 1,

    /// <summary>A stored document that changes and loses nothing.</summary>
    Change = 2,

    /// <summary>A stored document that is removed or loses something stored.</summary>
    Destructive = 3,
}
