using FastEndpoints;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Filters;

public class SensitivityFilter : IGlobalPostProcessor
{
    public async Task PostProcessAsync(IPostProcessorContext context, CancellationToken ct)
    {
        Console.WriteLine($"[SERVER] SensitivityFilter Running. Response Type: {context.Response?.GetType().Name}");
        // Check if the response is a Content object or list of Content objects
        // This is a simplified implementation. In a real scenario, we might need to inspect the response body more deeply
        // or use a custom serializer.

        if (context.Response is Content content)
        {
            ApplySensitivity(content, context.HttpContext);
        }
        else if (context.Response is barakoCMS.Features.Content.Get.Response getResponse)
        {
            // Map Get.Response to Content-like object or overload ApplySensitivity
            // Since we can't easily cast, we'll create a temporary wrapper or overload
            ApplySensitivity(getResponse, context.HttpContext);
        }
        else if (context.Response is IEnumerable<Content> contentList)
        {
            foreach (var item in contentList)
            {
                ApplySensitivity(item, context.HttpContext);
            }
        }

        await Task.CompletedTask;
    }

    private void ApplySensitivity(barakoCMS.Features.Content.Get.Response content, HttpContext httpContext)
    {
        var isSuperAdmin = httpContext.User.IsInRole("SuperAdmin");
        var isHr = httpContext.User.IsInRole("HR");

        Console.WriteLine($"[SERVER] Applying Sensitivity. ContentType: {content.ContentType}, IsSuperAdmin: {isSuperAdmin}, IsHr: {isHr}");
        Console.WriteLine($"[SERVER] Keys before: {string.Join(", ", content.Data.Keys)}");

        if (content.Sensitivity == SensitivityLevel.Hidden && !isSuperAdmin)
        {
            content.Data.Clear();
            content.ContentType = "HIDDEN";
            return;
        }

        if (content.ContentType == "AttendanceRecord")
        {
            if (!isSuperAdmin && content.Data.ContainsKey("SSN"))
            {
                Console.WriteLine("[SERVER] Removing SSN");
                content.Data.Remove("SSN");
            }
            Console.WriteLine($"[SERVER] Keys after: {string.Join(", ", content.Data.Keys)}");

            if (!isSuperAdmin && !isHr && content.Data.ContainsKey("BirthDay"))
            {
                content.Data["BirthDay"] = "***";
            }
        }

        if (content.Sensitivity == SensitivityLevel.Sensitive && !isSuperAdmin && !isHr)
        {
            content.Data.Clear();
        }
    }

    private void ApplySensitivity(Content content, HttpContext httpContext)
    {
        var isSuperAdmin = httpContext.User.IsInRole("SuperAdmin");
        var isHr = httpContext.User.IsInRole("HR");

        // 1. Document-Level Sensitivity
        if (content.Sensitivity == SensitivityLevel.Hidden && !isSuperAdmin)
        {
            content.Data.Clear();
            content.ContentType = "HIDDEN";
            return;
        }

        // 2. Field-Level Sensitivity (POC Implementation for AttendanceRecord)
        if (content.ContentType == "AttendanceRecord")
        {
            // SSN is Hidden: Only SuperAdmin can see it
            if (!isSuperAdmin && content.Data.ContainsKey("SSN"))
            {
                content.Data.Remove("SSN");
            }

            // BirthDay is Sensitive: Only HR or SuperAdmin can see it
            if (!isSuperAdmin && !isHr && content.Data.ContainsKey("BirthDay"))
            {
                content.Data["BirthDay"] = "***"; // Mask it
            }
            // Note: If user is HR, they see BirthDay. If SuperAdmin, they see everything.
        }

        // Fallback for generic Sensitive content
        if (content.Sensitivity == SensitivityLevel.Sensitive && !isSuperAdmin && !isHr)
        {
            content.Data.Clear();
        }
    }
}
