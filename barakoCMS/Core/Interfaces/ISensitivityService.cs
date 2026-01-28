using barakoCMS.Models;
using Microsoft.AspNetCore.Http;

namespace barakoCMS.Core.Interfaces;

public interface ISensitivityService
{
    /// <summary>
    /// Applies document-level and field-level sensitivity filtering to content.
    /// </summary>
    /// <param name="content">The content to filter</param>
    /// <param name="httpContext">HTTP context containing user claims</param>
    /// <param name="contentTypeDefinition">Optional content type definition with field sensitivity rules</param>
    void Apply(Content content, HttpContext httpContext, ContentTypeDefinition? contentTypeDefinition = null);

    /// <summary>
    /// Applies document-level and field-level sensitivity filtering to content response.
    /// </summary>
    /// <param name="response">The content response to filter</param>
    /// <param name="httpContext">HTTP context containing user claims</param>
    /// <param name="contentTypeDefinition">Optional content type definition with field sensitivity rules</param>
    void Apply(barakoCMS.Features.Content.Get.Response response, HttpContext httpContext, ContentTypeDefinition? contentTypeDefinition = null);

    /// <summary>
    /// Applies field-level sensitivity filtering to a data dictionary.
    /// </summary>
    /// <param name="data">The data dictionary to filter</param>
    /// <param name="httpContext">HTTP context containing user claims</param>
    /// <param name="contentTypeDefinition">Content type definition with field sensitivity rules</param>
    void ApplyFieldSensitivity(Dictionary<string, object> data, HttpContext httpContext, ContentTypeDefinition? contentTypeDefinition);
}
