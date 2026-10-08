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

    /// <summary>Logs one warning for each sealed service among what <paramref name="module"/> registered.</summary>
    public static void WarnAbout(IBarakoModule module, IEnumerable<ServiceDescriptor> registered)
    {
        foreach (var type in registered.Select(d => d.ServiceType).Where(Types.Contains).Distinct())
        {
            Log.Warning(Warning, module.Name, type.FullName);
        }
    }
}
