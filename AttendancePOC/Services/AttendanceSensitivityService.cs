using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using Microsoft.Extensions.Configuration;

namespace AttendancePOC.Services;

public class AttendanceSensitivityService : ISensitivityService
{
    private readonly IConfiguration _configuration;

    public AttendanceSensitivityService(IConfiguration configuration)
    {
        Console.WriteLine("[ATTENDANCE] Service Instantiated");
        _configuration = configuration;
    }

    public void Apply(Content content, HttpContext httpContext)
    {
        // Default behavior for Document-Level Sensitivity
        var isSuperAdmin = httpContext.User.IsInRole("SuperAdmin");
        if (content.Sensitivity == SensitivityLevel.Hidden && !isSuperAdmin)
        {
            content.Data.Clear();
            content.ContentType = "HIDDEN";
            return;
        }
        if (content.Sensitivity == SensitivityLevel.Sensitive && !isSuperAdmin) 
        {
             // Check if user has specific access via config? 
             // For now, keep default behavior: Sensitive = Metadata only for non-superadmin (unless handled below)
             // But wait, AttendanceRecord is Sensitive.
             // If we clear data here, we lose the chance to mask fields.
             // So we should only clear if it's NOT handled by specific policies.
        }
    }

    public void Apply(barakoCMS.Features.Content.Get.Response content, HttpContext httpContext)
    {
        var isSuperAdmin = httpContext.User.IsInRole("SuperAdmin");

        // 1. Document-Level Check
        if (content.Sensitivity == SensitivityLevel.Hidden && !isSuperAdmin)
        {
            content.Data.Clear();
            content.ContentType = "HIDDEN";
            return;
        }

        // 2. Policy-Based Field Sensitivity
        var policies = _configuration.GetSection($"SensitivityPolicies:{content.ContentType}");
        if (policies.Exists())
        {
            foreach (var fieldSection in policies.GetChildren())
            {
                var fieldName = fieldSection.Key;
                var action = fieldSection["Action"];
                var allowedRoles = fieldSection.GetSection("AllowedRoles").Get<string[]>() ?? Array.Empty<string>();
                var maskValue = fieldSection["MaskValue"] ?? "***";

                // Check if user has ANY of the allowed roles
                var hasAccess = isSuperAdmin || allowedRoles.Any(role => httpContext.User.IsInRole(role));
                
                Console.WriteLine($"[SENSITIVITY] Field: {fieldName}, Action: {action}, Allowed: {string.Join(",", allowedRoles)}, UserAccess: {hasAccess}");

                if (!hasAccess && content.Data.ContainsKey(fieldName))
                {
                    if (action == "Remove")
                    {
                        Console.WriteLine($"[SENSITIVITY] Removing {fieldName}");
                        content.Data.Remove(fieldName);
                    }
                    else if (action == "Mask")
                    {
                        Console.WriteLine($"[SENSITIVITY] Masking {fieldName}");
                        content.Data[fieldName] = maskValue;
                    }
                }
            }
        }
        
        // 3. Fallback for generic Sensitive content (if not handled above)
        // If content is Sensitive, and user is not SuperAdmin, and we haven't defined policies...
        // This is tricky. If we define policies, we assume we handled it.
        // But if content.Sensitivity is Sensitive, we might still want to hide everything else?
        // For POC, we assume Policy overrides generic Sensitive flag for specific fields.
    }
}
