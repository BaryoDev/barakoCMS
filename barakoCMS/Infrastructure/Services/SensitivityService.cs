using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using Microsoft.AspNetCore.Http;

namespace barakoCMS.Infrastructure.Services;

public class SensitivityService : ISensitivityService
{
    public void Apply(Content content, HttpContext httpContext, ContentTypeDefinition? contentTypeDefinition = null)
    {
        var isSuperAdmin = httpContext.User.IsInRole("SuperAdmin");
        var isHr = httpContext.User.IsInRole("HR");

        // 1. Document-level sensitivity
        if (content.Sensitivity == SensitivityLevel.Hidden && !isSuperAdmin)
        {
            content.Data.Clear();
            content.ContentType = "HIDDEN";
            return;
        }

        if (content.Sensitivity == SensitivityLevel.Sensitive && !isSuperAdmin && !isHr)
        {
            content.Data.Clear();
            return;
        }

        // 2. Field-level sensitivity
        ApplyFieldSensitivity(content.Data, httpContext, contentTypeDefinition);
    }

    public void Apply(barakoCMS.Features.Content.Get.Response content, HttpContext httpContext, ContentTypeDefinition? contentTypeDefinition = null)
    {
        var isSuperAdmin = httpContext.User.IsInRole("SuperAdmin");
        var isHr = httpContext.User.IsInRole("HR");

        // 1. Document-level sensitivity
        if (content.Sensitivity == SensitivityLevel.Hidden && !isSuperAdmin)
        {
            content.Data.Clear();
            content.ContentType = "HIDDEN";
            return;
        }

        if (content.Sensitivity == SensitivityLevel.Sensitive && !isSuperAdmin && !isHr)
        {
            content.Data.Clear();
            return;
        }

        // 2. Field-level sensitivity
        ApplyFieldSensitivity(content.Data, httpContext, contentTypeDefinition);
    }

    public void ApplyFieldSensitivity(Dictionary<string, object> data, HttpContext httpContext, ContentTypeDefinition? contentTypeDefinition)
    {
        if (contentTypeDefinition == null || contentTypeDefinition.Fields == null)
        {
            return;
        }

        var userRoles = GetUserRoles(httpContext);
        var isSuperAdmin = userRoles.Contains("SuperAdmin");

        // SuperAdmin always sees everything
        if (isSuperAdmin)
        {
            return;
        }

        foreach (var field in contentTypeDefinition.Fields)
        {
            // Skip fields without sensitivity settings
            if (field.Sensitivity == null)
            {
                continue;
            }

            // Check if the field exists in the data
            if (!data.ContainsKey(field.Name))
            {
                continue;
            }

            // Check if user has any of the allowed roles
            var hasAccess = field.Sensitivity.AllowedRoles.Any(role =>
                userRoles.Contains(role, StringComparer.OrdinalIgnoreCase));

            if (!hasAccess)
            {
                // Apply the sensitivity action
                switch (field.Sensitivity.Action)
                {
                    case FieldSensitivityAction.Remove:
                        data.Remove(field.Name);
                        break;

                    case FieldSensitivityAction.Mask:
                        data[field.Name] = field.Sensitivity.MaskValue;
                        break;
                }
            }
        }
    }

    private HashSet<string> GetUserRoles(HttpContext httpContext)
    {
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var claim in httpContext.User.Claims)
        {
            if (claim.Type == System.Security.Claims.ClaimTypes.Role ||
                claim.Type == "role" ||
                claim.Type == "roles")
            {
                roles.Add(claim.Value);
            }
        }

        return roles;
    }
}
