using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace AttendancePOC.Services;

/// <summary>
/// POC sensitivity service driven by appsettings <c>SensitivityPolicies:{contentType}</c> instead of
/// the field schema. Kept as an example of overriding the default <see cref="ISensitivityService"/>;
/// production apps should prefer the schema-driven service in barakoCMS core.
/// </summary>
public class AttendanceSensitivityService : ISensitivityService
{
    private readonly IConfiguration _configuration;

    public AttendanceSensitivityService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public bool Apply(string contentType, SensitivityLevel level, IDictionary<string, object> data, HttpContext httpContext)
    {
        var isSuperAdmin = httpContext.User.IsInRole("SuperAdmin");

        // Document-level: Hidden blanks everything for non-SuperAdmin.
        if (level == SensitivityLevel.Hidden && !isSuperAdmin)
        {
            data.Clear();
            return true;
        }

        // Field-level from config policies.
        var policies = _configuration.GetSection($"SensitivityPolicies:{contentType}");
        if (policies.Exists())
        {
            foreach (var fieldSection in policies.GetChildren())
            {
                var fieldName = fieldSection.Key;
                if (!data.ContainsKey(fieldName) || CallerAllowed(fieldSection, httpContext, isSuperAdmin))
                    continue;

                if (fieldSection["Action"] == "Remove")
                    data.Remove(fieldName);
                else
                    data[fieldName] = fieldSection["MaskValue"] ?? "***";
            }
        }

        return false;
    }

    public void ApplyWrite(string contentType, IDictionary<string, object> incoming, IReadOnlyDictionary<string, object>? existing, HttpContext httpContext)
    {
        var isSuperAdmin = httpContext.User.IsInRole("SuperAdmin");
        if (isSuperAdmin)
            return;

        var policies = _configuration.GetSection($"SensitivityPolicies:{contentType}");
        if (!policies.Exists())
            return;

        foreach (var fieldSection in policies.GetChildren())
        {
            var fieldName = fieldSection.Key;
            if (CallerAllowed(fieldSection, httpContext, isSuperAdmin))
                continue;

            // Cannot see it, cannot set it: revert on update, drop on create.
            if (existing != null && existing.TryGetValue(fieldName, out var current))
                incoming[fieldName] = current;
            else
                incoming.Remove(fieldName);
        }
    }

    private static bool CallerAllowed(IConfigurationSection fieldSection, HttpContext httpContext, bool isSuperAdmin)
    {
        if (isSuperAdmin)
            return true;
        var allowedRoles = fieldSection.GetSection("AllowedRoles").Get<string[]>() ?? Array.Empty<string>();
        return allowedRoles.Any(role => httpContext.User.IsInRole(role));
    }
}
