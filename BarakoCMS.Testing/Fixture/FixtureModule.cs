using barakoCMS.Core.Interfaces;
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
}

/// <summary>The one capability <see cref="FixtureModule"/>'s endpoint asks for.</summary>
public static class FixtureCapabilities
{
    public const string ViewFixture = "view_fixture";
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
