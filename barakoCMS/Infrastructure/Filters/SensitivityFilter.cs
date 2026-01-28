using FastEndpoints;
using Marten;
using barakoCMS.Models;
using barakoCMS.Core.Interfaces;

namespace barakoCMS.Infrastructure.Filters;

public class SensitivityFilter : IGlobalPostProcessor
{
    public async Task PostProcessAsync(IPostProcessorContext context, CancellationToken ct)
    {
        // Resolve dependencies from DI
        var querySession = context.HttpContext.RequestServices.GetService<IQuerySession>();
        var sensitivityService = context.HttpContext.RequestServices.GetService<ISensitivityService>();

        if (sensitivityService == null)
        {
            // Fallback if service not registered - should not happen in production
            return;
        }

        if (context.Response is Content content)
        {
            var contentTypeDef = await GetContentTypeDefinitionAsync(querySession, content.ContentType, ct);
            sensitivityService.Apply(content, context.HttpContext, contentTypeDef);
        }
        else if (context.Response is barakoCMS.Features.Content.Get.Response getResponse)
        {
            var contentTypeDef = await GetContentTypeDefinitionAsync(querySession, getResponse.ContentType, ct);
            sensitivityService.Apply(getResponse, context.HttpContext, contentTypeDef);
        }
        else if (context.Response is IEnumerable<Content> contentList)
        {
            // Cache content type definitions to avoid repeated lookups
            var contentTypeCache = new Dictionary<string, ContentTypeDefinition?>();

            foreach (var item in contentList)
            {
                if (!contentTypeCache.TryGetValue(item.ContentType, out var contentTypeDef))
                {
                    contentTypeDef = await GetContentTypeDefinitionAsync(querySession, item.ContentType, ct);
                    contentTypeCache[item.ContentType] = contentTypeDef;
                }

                sensitivityService.Apply(item, context.HttpContext, contentTypeDef);
            }
        }
    }

    private async Task<ContentTypeDefinition?> GetContentTypeDefinitionAsync(
        IQuerySession? querySession,
        string contentTypeName,
        CancellationToken ct)
    {
        if (querySession == null || string.IsNullOrEmpty(contentTypeName))
        {
            return null;
        }

        try
        {
            return await querySession
                .Query<ContentTypeDefinition>()
                .FirstOrDefaultAsync(x => x.Name == contentTypeName, ct);
        }
        catch
        {
            // Log and continue without field-level sensitivity if lookup fails
            return null;
        }
    }
}
