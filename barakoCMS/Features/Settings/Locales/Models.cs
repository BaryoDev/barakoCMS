using barakoCMS.Models;

namespace barakoCMS.Features.Settings.Locales;

internal sealed class LocaleSettingsResponse
{
    public string DefaultLocale { get; set; } = string.Empty;
    public List<string> Locales { get; set; } = [];
    public Dictionary<string, List<string>> FallbackChains { get; set; } = new();
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    internal static LocaleSettingsResponse From(LocaleSettings? row) => row is null
        ? new LocaleSettingsResponse()
        : new LocaleSettingsResponse
        {
            DefaultLocale = row.DefaultLocale,
            Locales = [.. row.Locales],
            FallbackChains = row.FallbackChains.ToDictionary(p => p.Key, p => new List<string>(p.Value)),
            UpdatedAt = row.UpdatedAt,
            UpdatedBy = row.UpdatedBy,
        };
}

internal sealed class UpdateLocaleSettingsRequest
{
    public string DefaultLocale { get; set; } = string.Empty;
    public List<string> Locales { get; set; } = [];
    public Dictionary<string, List<string>> FallbackChains { get; set; } = new();
}
