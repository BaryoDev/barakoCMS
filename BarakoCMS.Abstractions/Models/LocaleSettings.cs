namespace barakoCMS.Models;

/// <summary>
/// The languages a tenant publishes in. One row per tenant, at <see cref="SingletonId"/>.
/// </summary>
/// <remarks>
/// Settings only. Nothing reads these yet to pick a translation; that comes with per-field locale
/// values (#98).
/// </remarks>
public class LocaleSettings
{
    public static readonly Guid SingletonId = new("3f6b1c52-8a4e-4d7b-b2a9-5e0c7d1f9a64");

    public Guid Id { get; set; } = SingletonId;

    /// <summary>The locale used when a caller names none. Empty when no languages are configured.</summary>
    public string DefaultLocale { get; set; } = string.Empty;

    /// <summary>The language codes the tenant offers, BCP 47 shaped, for example <c>en</c> or <c>pt-BR</c>.</summary>
    public List<string> Locales { get; set; } = [];

    /// <summary>For a locale, the ordered codes to try when it has no value.</summary>
    public Dictionary<string, List<string>> FallbackChains { get; set; } = new();

    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}
