using barakoCMS.Models;
using Microsoft.AspNetCore.Http;

namespace barakoCMS.Core.Interfaces;

public interface ISensitivityService
{
    void Apply(Content content, HttpContext httpContext);
    void Apply(barakoCMS.Features.Content.Get.Response response, HttpContext httpContext);
}
