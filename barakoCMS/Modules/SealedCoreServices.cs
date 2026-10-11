using barakoCMS.Core.Interfaces;
using barakoCMS.Infrastructure.Services;
using Serilog;

namespace barakoCMS.Modules;

/// <summary>
/// The core services a module cannot replace, and the warning a module gets for trying.
/// </summary>
/// <remarks>
/// Modules register before core, and core registers these with <c>AddScoped</c>, so the core one is
/// the last registration and the one every resolve gets. That is deliberate: each of them holds an
/// invariant (one writer for all content, one place masking happens, one place the sourcing decision
/// is read, one way template variables resolve). What used to be silent is a module that registered
/// one anyway and never had its implementation called, so it is named at startup (#697).
///
/// The seams a module may replace are registered with <c>TryAdd</c> instead: <see cref="IEmailService"/>,
/// <see cref="ISmsService"/>, <see cref="IFileStore"/>, <see cref="IDeviceGate"/>,
/// <see cref="IOtpService"/>, <see cref="IEmailVerificationService"/> and <see cref="IEmailSettingsProvider"/>.
/// </remarks>
internal static class SealedCoreServices
{
    public const string Warning =
        "Module {Module} registers {Service}, which core does not let a module replace. Core's own "
        + "implementation is used and the module's is never called.";

    public static readonly IReadOnlyList<Type> Types =
    [
        typeof(IContentWriter),
        typeof(ISensitivityService),
        typeof(IContentSourcingPolicy),
        typeof(ITemplateVariableExtractor),
    ];

    /// <summary>The descriptors registered so far, by reference, to tell later what a module added.</summary>
    public static IReadOnlySet<ServiceDescriptor> Snapshot(IServiceCollection services) =>
        new HashSet<ServiceDescriptor>(services, ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Logs one warning for each sealed service <paramref name="module"/> registered: each
    /// descriptor in <paramref name="services"/> that is not in <paramref name="before"/>.
    /// </summary>
    /// <remarks>
    /// By reference and not by count, so a module that inserts at the front, or removes one entry
    /// and adds another, is still seen. A keyed registration is left alone: core resolves these
    /// without a key, so a keyed one replaces nothing and is the module's own business.
    /// </remarks>
    public static void WarnAbout(IBarakoModule module, IServiceCollection services, IReadOnlySet<ServiceDescriptor> before)
    {
        var added = services
            .Where(d => !d.IsKeyedService && !before.Contains(d))
            .Select(d => d.ServiceType)
            .Where(Types.Contains)
            .Distinct();

        foreach (var type in added)
        {
            Log.Warning(Warning, module.Name, type.FullName);
        }
    }
}
