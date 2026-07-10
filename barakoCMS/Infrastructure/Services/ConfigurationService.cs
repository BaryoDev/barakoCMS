using Marten;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Services;

public interface IConfigurationService
{
    Task<T> GetConfigValueAsync<T>(string key, T defaultValue, CancellationToken ct = default);
}

public class ConfigurationService : IConfigurationService
{
    private readonly IDocumentSession _session;
    private readonly IConfiguration _configuration;

    public ConfigurationService(IDocumentSession session, IConfiguration configuration)
    {
        _session = session;
        _configuration = configuration;
    }

    public async Task<T> GetConfigValueAsync<T>(string key, T defaultValue, CancellationToken ct = default)
    {
        // 1. Check database for override (an admin-editable, possibly malformed value).
        var setting = await _session.Query<SystemSetting>()
            .FirstOrDefaultAsync(s => s.Key == key, ct);

        if (setting != null && TryConvertValue<T>(setting.Value, out var dbValue))
        {
            return dbValue;
        }

        // 2. Check environment variable (supports both __ and : separators)
        var envValue = _configuration[key] ?? _configuration[key.Replace("__", ":")];
        if (envValue != null && TryConvertValue<T>(envValue, out var envConverted))
        {
            return envConverted;
        }

        // 3. Fall back to the default. Malformed or unsupported values fall through here rather
        //    than throwing, so a bad admin-entered setting cannot crash its consumers.
        return defaultValue;
    }

    private static bool TryConvertValue<T>(string value, out T result)
    {
        result = default!;
        var targetType = typeof(T);

        if (targetType == typeof(bool) && bool.TryParse(value, out var b))
        {
            result = (T)(object)b;
            return true;
        }
        if (targetType == typeof(int) && int.TryParse(value, out var i))
        {
            result = (T)(object)i;
            return true;
        }
        if (targetType == typeof(string))
        {
            result = (T)(object)value;
            return true;
        }

        return false;
    }
}
