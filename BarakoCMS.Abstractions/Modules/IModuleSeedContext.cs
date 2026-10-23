using barakoCMS.Core.Interfaces;
using barakoCMS.Models;

namespace barakoCMS.Modules;

/// <summary>
/// What a module is given to seed baseline data, with no persistence type in it.
/// </summary>
/// <remarks>
/// <see cref="IBarakoModule.SeedAsync(IModuleSeedContext, CancellationToken)"/> receives this. It
/// is the module's alone and the host commits it once the seed returns: nothing here saves.
/// Everything is additive and idempotent, so a restart is not a write.
/// </remarks>
public interface IModuleSeedContext
{
    /// <summary>The services of the scope this seed runs in.</summary>
    IServiceProvider Services { get; }

    /// <summary>Reads content that is already committed.</summary>
    IContentReader Reader { get; }

    /// <summary>Records content the seed creates. Stages, does not commit.</summary>
    IContentWriter Writer { get; }

    /// <summary>
    /// Stores the content type definition when no type of that name exists, and leaves an existing
    /// one alone, so a host that customised it keeps its changes.
    /// </summary>
    /// <returns>True when the definition was added.</returns>
    Task<bool> EnsureContentTypeAsync(ContentTypeDefinition definition, CancellationToken cancellationToken);

    /// <summary>
    /// Makes sure the module's own role exists, found by <paramref name="id"/> before by name, and
    /// only among the deployment's roles, so a tenant's role of the same name is never taken over.
    /// A role that was deleted comes back on start. A renamed role keeps its permissions.
    /// </summary>
    /// <returns>True when a role was added.</returns>
    Task<bool> EnsureRoleAsync(Guid id, string name, string description, CancellationToken cancellationToken);
}
