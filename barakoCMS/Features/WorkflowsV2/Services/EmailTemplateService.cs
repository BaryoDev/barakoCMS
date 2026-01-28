using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace barakoCMS.Features.WorkflowsV2.Services;

/// <summary>
/// Filesystem-based email template service.
/// </summary>
public class EmailTemplateService : IEmailTemplateService
{
    private readonly string _templatesPath;
    private readonly ILogger<EmailTemplateService> _logger;
    private readonly Dictionary<string, EmailTemplate> _cache = new();
    private DateTime _lastCacheRefresh = DateTime.MinValue;
    private readonly TimeSpan _cacheTimeout = TimeSpan.FromMinutes(5);

    public EmailTemplateService(string templatesPath, ILogger<EmailTemplateService> logger)
    {
        _templatesPath = templatesPath;
        _logger = logger;

        // Ensure directory exists
        if (!Directory.Exists(_templatesPath))
        {
            Directory.CreateDirectory(_templatesPath);
        }
    }

    public async Task<EmailTemplate?> GetTemplateAsync(string name)
    {
        await RefreshCacheIfNeeded();

        if (_cache.TryGetValue(name.ToLowerInvariant(), out var template))
        {
            return template;
        }

        // Try to load from file
        var filePath = GetTemplatePath(name);
        if (File.Exists(filePath))
        {
            template = await LoadTemplateFromFileAsync(filePath);
            if (template != null)
            {
                _cache[name.ToLowerInvariant()] = template;
                return template;
            }
        }

        return null;
    }

    public async Task<List<EmailTemplate>> ListTemplatesAsync()
    {
        await RefreshCacheIfNeeded();
        return _cache.Values.ToList();
    }

    public string RenderTemplate(string template, Dictionary<string, object> variables)
    {
        if (string.IsNullOrEmpty(template))
            return template;

        // Replace {{variable}} patterns
        var result = Regex.Replace(template, @"\{\{([^}]+)\}\}", match =>
        {
            var path = match.Groups[1].Value.Trim();
            var value = GetNestedValue(variables, path.Split('.'));
            return value?.ToString() ?? "";
        });

        // Handle simple conditionals: {{#if variable}}content{{/if}}
        result = Regex.Replace(result, @"\{\{#if\s+([^}]+)\}\}([\s\S]*?)\{\{/if\}\}", match =>
        {
            var condition = match.Groups[1].Value.Trim();
            var content = match.Groups[2].Value;
            var value = GetNestedValue(variables, condition.Split('.'));

            if (IsTruthy(value))
            {
                return content;
            }
            return "";
        });

        // Handle loops: {{#each items}}content with {{this}}{{/each}}
        result = Regex.Replace(result, @"\{\{#each\s+([^}]+)\}\}([\s\S]*?)\{\{/each\}\}", match =>
        {
            var arrayPath = match.Groups[1].Value.Trim();
            var itemTemplate = match.Groups[2].Value;
            var arrayValue = GetNestedValue(variables, arrayPath.Split('.'));

            if (arrayValue is IEnumerable<object> items)
            {
                var itemResults = new List<string>();
                foreach (var item in items)
                {
                    var itemStr = itemTemplate.Replace("{{this}}", item?.ToString() ?? "");

                    // Also replace {{this.property}} for object items
                    if (item is Dictionary<string, object> itemDict)
                    {
                        itemStr = Regex.Replace(itemStr, @"\{\{this\.([^}]+)\}\}", m =>
                        {
                            var prop = m.Groups[1].Value;
                            return itemDict.TryGetValue(prop, out var v) ? v?.ToString() ?? "" : "";
                        });
                    }

                    itemResults.Add(itemStr);
                }
                return string.Join("", itemResults);
            }

            return "";
        });

        return result;
    }

    public async Task SaveTemplateAsync(EmailTemplate template)
    {
        var filePath = GetTemplatePath(template.Name);
        template.UpdatedAt = DateTime.UtcNow;

        var json = JsonSerializer.Serialize(template, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        await File.WriteAllTextAsync(filePath, json);

        _cache[template.Name.ToLowerInvariant()] = template;

        _logger.LogInformation("Saved email template: {Name}", template.Name);
    }

    public async Task DeleteTemplateAsync(string name)
    {
        var filePath = GetTemplatePath(name);

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
            _cache.Remove(name.ToLowerInvariant());
            _logger.LogInformation("Deleted email template: {Name}", name);
        }

        await Task.CompletedTask;
    }

    private async Task RefreshCacheIfNeeded()
    {
        if (DateTime.UtcNow - _lastCacheRefresh < _cacheTimeout)
            return;

        _cache.Clear();

        if (!Directory.Exists(_templatesPath))
            return;

        var files = Directory.GetFiles(_templatesPath, "*.json");

        foreach (var file in files)
        {
            try
            {
                var template = await LoadTemplateFromFileAsync(file);
                if (template != null)
                {
                    _cache[template.Name.ToLowerInvariant()] = template;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load email template: {File}", file);
            }
        }

        _lastCacheRefresh = DateTime.UtcNow;
        _logger.LogDebug("Refreshed email template cache with {Count} templates", _cache.Count);
    }

    private async Task<EmailTemplate?> LoadTemplateFromFileAsync(string filePath)
    {
        var json = await File.ReadAllTextAsync(filePath);
        return JsonSerializer.Deserialize<EmailTemplate>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    private string GetTemplatePath(string name)
    {
        // Sanitize name to prevent path traversal
        var safeName = Regex.Replace(name, @"[^a-zA-Z0-9\-_]", "");
        return Path.Combine(_templatesPath, $"{safeName}.json");
    }

    private object? GetNestedValue(Dictionary<string, object> data, string[] path)
    {
        if (data == null || path.Length == 0)
            return null;

        object? current = data;

        foreach (var key in path)
        {
            if (current is Dictionary<string, object> dict)
            {
                if (!dict.TryGetValue(key, out current))
                {
                    // Try case-insensitive
                    var matchingKey = dict.Keys.FirstOrDefault(k =>
                        k.Equals(key, StringComparison.OrdinalIgnoreCase));

                    if (matchingKey == null)
                        return null;

                    current = dict[matchingKey];
                }
            }
            else if (current is JsonElement je && je.ValueKind == JsonValueKind.Object)
            {
                if (je.TryGetProperty(key, out var prop))
                {
                    current = prop;
                }
                else
                {
                    return null;
                }
            }
            else
            {
                return null;
            }
        }

        // Unwrap JsonElement
        if (current is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => current
            };
        }

        return current;
    }

    private bool IsTruthy(object? value)
    {
        if (value == null)
            return false;

        if (value is bool b)
            return b;

        if (value is string s)
            return !string.IsNullOrEmpty(s);

        if (value is int i)
            return i != 0;

        if (value is long l)
            return l != 0;

        if (value is double d)
            return d != 0;

        if (value is IEnumerable<object> e)
            return e.Any();

        return true;
    }
}
