using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using Microsoft.AspNetCore.Http;

namespace barakoCMS.Infrastructure.Services;

public class SensitivityService : ISensitivityService
{
    public void Apply(Content content, HttpContext httpContext)
    {
        var isSuperAdmin = httpContext.User.IsInRole("SuperAdmin");
        var isHr = httpContext.User.IsInRole("HR");

        if (content.Sensitivity == SensitivityLevel.Hidden && !isSuperAdmin)
        {
            content.Data.Clear();
            content.ContentType = "HIDDEN";
            return;
        }

        if (content.Sensitivity == SensitivityLevel.Sensitive && !isSuperAdmin && !isHr)
        {
             content.Data.Clear();
        }
    }

    public void Apply(barakoCMS.Features.Content.Get.Response content, HttpContext httpContext)
    {
        Console.WriteLine("[CORE] SensitivityService Running");
        var isSuperAdmin = httpContext.User.IsInRole("SuperAdmin");
        var isHr = httpContext.User.IsInRole("HR");

        if (content.Sensitivity == SensitivityLevel.Hidden && !isSuperAdmin)
        {
            content.Data.Clear();
            content.ContentType = "HIDDEN";
            return;
        }

        if (content.Sensitivity == SensitivityLevel.Sensitive && !isSuperAdmin && !isHr)
        {
             content.Data.Clear();
        }
    }
}
