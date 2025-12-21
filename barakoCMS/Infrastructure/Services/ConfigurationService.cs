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
        // 1. Check database for override
        var setting = await _session.Query<SystemSetting>()
            .FirstOrDefaultAsync(s => s.Key == key, ct);

        if (setting != null)
        {
            return ConvertValue<T>(setting.Value);
        }

        // 2. Check environment variable (supports both __ and : separators)
        var envValue = _configuration[key] ?? _configuration[key.Replace("__", ":")];
        if (envValue != null)
        {
            return ConvertValue<T>(envValue);
        }

        // 3. Return default
        return defaultValue;
    }

    private static T ConvertValue<T>(string value)
    {
        var targetType = typeof(T);

        if (targetType == typeof(bool))
        {
            return (T)(object)bool.Parse(value);
        }
        if (targetType == typeof(int))
        {
            return (T)(object)int.Parse(value);
        }
        if (targetType == typeof(string))
        {
            return (T)(object)value;
        }

        throw new NotSupportedException($"Type {targetType} is not supported for configuration values");
    }
}
