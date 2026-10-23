using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using barakoCMS.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BarakoCMS.Testing.Fixture;

/// <summary>
/// A small module for tests of core mechanisms that need a module and nothing else: one capability,
/// one content lifecycle hook and one endpoint that asks for the capability. It lets a core test
/// prove a mechanism without borrowing a shipped domain module.
/// </summary>
public sealed class FixtureModule : IBarakoModule
{
    public string Name => "Fixture";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddScoped<IContentLifecycleHook, FixtureAmountHook>();

    public Task SeedAsync(IModuleSeedContext context, CancellationToken ct) =>
        FixtureCapabilities.Defaults.GrantAsync(context, ct);

    [Obsolete("Override SeedAsync(IModuleSeedContext, CancellationToken), which names no Marten type. "
              + "Removal planned for barakoCMS 6.0.0.")]
    public Task SeedAsync(Marten.IDocumentSession session, IServiceProvider services, CancellationToken ct)
    {
#pragma warning disable CS0618
        return SeedAsync(ModuleSeedContexts.Over(session, services), ct);
#pragma warning restore CS0618
    }
}

/// <summary>The one capability <see cref="FixtureModule"/>'s endpoint asks for.</summary>
public static class FixtureCapabilities
{
    public const string ViewFixture = "view_fixture";

    internal static readonly CapabilityDefaults Defaults = CapabilityDefaults.For(ViewFixture).GrantedTo(SystemRoles.Admin);
}

/// <summary>
/// Rejects an entry of <see cref="ContentTypeName"/> whose <c>Amount</c> is negative, and an entry
/// with no <c>Amount</c> at all.
/// </summary>
public sealed class FixtureAmountHook : IContentLifecycleHook
{
    public const string ContentTypeName = "fixture-entry";

    public const string NegativeAmountMessage = "Amount must not be negative.";

    public const string MissingAmountMessage = "Amount is required.";

    public string ContentType => ContentTypeName;

    public Task<IReadOnlyList<string>> BeforeSaveAsync(IContentHookContext context, CancellationToken ct)
    {
        var errors = new List<string>();

        if (!context.Data.TryGetValue("Amount", out var raw) || raw is null)
        {
            errors.Add(MissingAmountMessage);
        }
        else if (decimal.TryParse(
                     Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture),
                     System.Globalization.NumberStyles.Number,
                     System.Globalization.CultureInfo.InvariantCulture,
                     out var amount)
                 && amount < 0)
        {
            errors.Add(NegativeAmountMessage);
        }

        return Task.FromResult<IReadOnlyList<string>>(errors);
    }
}
