using FastEndpoints;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Filters;

public class SensitivityFilter : IGlobalPostProcessor
{
    public async Task PostProcessAsync(IPostProcessorContext context, CancellationToken ct)
    {
        // Check if the response is a Content object or list of Content objects
        // This is a simplified implementation. In a real scenario, we might need to inspect the response body more deeply
        // or use a custom serializer.
        
        // For this MVP, we assume the response is the Content object itself if it's a GET request
        if (context.Response is Content content)
        {
            ApplySensitivity(content, context.HttpContext);
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

    private void ApplySensitivity(Content content, HttpContext httpContext)
    {
        // TODO: Get actual user roles from HttpContext
        var isSuperAdmin = httpContext.User.IsInRole("SuperAdmin");

        if (content.Sensitivity == SensitivityLevel.Hidden && !isSuperAdmin)
        {
            // If hidden and not super admin, we might want to return null or throw an error, 
            // but since this is a post-processor, the response is already generated.
            // We can clear the data.
            content.Data.Clear();
            content.ContentType = "HIDDEN";
        }
        else if (content.Sensitivity == SensitivityLevel.Sensitive && !isSuperAdmin)
        {
            // If sensitive, maybe we hide specific fields? 
            // For now, let's say sensitive means "Read Only" or "Partial View"
            // The requirement said: "cannot be viewed, or edited or deleted except by superadmin role"
            // So if it's Sensitive, we treat it similarly to Hidden for non-superadmins for now, 
            // or maybe we just mask certain fields. 
            // Let's assume Sensitive means "Metadata only"
            content.Data.Clear();
        }
    }
}
