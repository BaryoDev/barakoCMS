namespace barakoCMS.Modules;

/// <summary>
/// The version of the module contract this build of core implements.
/// </summary>
/// <remarks>
/// <para>
/// A module author needs to know two things that documentation alone cannot tell them: which parts
/// of <see cref="IBarakoModule"/> they can rely on, and how they will find out when that changes.
/// This is the second half. <c>MODULES.md</c> is the first.
/// </para>
/// <para>
/// <b>What the contract covers.</b> Every member of <see cref="IBarakoModule"/>, the shape of
/// <see cref="IModuleSchema"/>, and the order in which core calls them. Nothing else. A module that
/// reaches past those into core's own services is not using the contract and this version says
/// nothing about it.
/// </para>
/// <para>
/// <b>What moves it.</b> Removing a member, changing a signature, or changing when core calls a hook
/// relative to the others. Adding a member with a default implementation does not, because a module
/// compiled against the previous version keeps working.
/// </para>
/// <para>
/// <b>What it is not.</b> Not the CMS version, and deliberately not tied to it. Core can go from
/// 3.21 to 4.0 without touching the contract, and a contract change can land in a minor. Coupling
/// the two would mean either a major release every time a module hook gained a parameter, or a
/// silent contract change inside a patch.
/// </para>
/// </remarks>
public static class ModuleContract
{
    /// <summary>
    /// The contract version this core implements. Compare against
    /// <see cref="IBarakoModule.ContractVersion"/> to see what a module was written for.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// The oldest contract version this core still accepts. Modules declaring an older one are
    /// refused rather than loaded and left to fail somewhere less obvious.
    /// </summary>
    public const int MinimumSupported = 1;
}
