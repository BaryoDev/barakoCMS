using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// Provides extraction and resolution of template variables for workflow actions.
/// Supports both system variables (id, contentType, status, etc.) and dynamic
/// data field variables from content.
/// </summary>
public interface ITemplateVariableExtractor
{
    /// <summary>
    /// Retrieves all available template variables for a specific content type.
    /// </summary>
    /// <param name="contentType">
    /// The content type name to extract data field variables from.
    /// If no content of this type exists, only system variables will be returned.
    /// </param>
    /// <param name="ct">Cancellation token for the asynchronous operation.</param>
    /// <returns>
    /// A <see cref="TemplateVariableCollection"/> containing both system variables
    /// (always present) and content-specific data field variables.
    /// </returns>
    /// <remarks>
    /// This method queries the database for a sample content item of the specified type
    /// to extract available data fields. The results can be used for autocomplete
    /// in workflow configuration interfaces.
    /// </remarks>
    Task<TemplateVariableCollection> GetVariablesAsync(string contentType, CancellationToken ct = default);

    /// <summary>
    /// Resolves all template variables in a string using actual content values.
    /// </summary>
    /// <param name="template">
    /// The template string containing variables in {{variable}} syntax.
    /// Example: "Order {{data.OrderNumber}} created at {{createdAt}}"
    /// </param>
    /// <param name="content">
    /// The content object providing values for variable resolution.
    /// Must not be null.
    /// </param>
    /// <returns>
    /// The template string with all variables replaced with their actual values.
    /// Variables that don't exist in the content remain unchanged.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="content"/> is null.
    /// </exception>
    /// <remarks>
    /// Supports system variables (id, contentType, status, createdAt, updatedAt)
    /// and dynamic data field variables (data.FieldName). Variable replacement
    /// is performed using StringBuilder for optimal performance.
    /// </remarks>
    string ResolveVariables(string template, Content content);
}

/// <summary>
/// Extracts and documents available template variables for workflows.
/// </summary>
public class TemplateVariableExtractor : ITemplateVariableExtractor
{
    private readonly IDocumentSession _session;

    public TemplateVariableExtractor(IDocumentSession session)
    {
        _session = session;
    }

    public async Task<TemplateVariableCollection> GetVariablesAsync(string contentType, CancellationToken ct = default)
    {
        var collection = new TemplateVariableCollection
        {
            SystemVariables = GetSystemVariables()
        };

        // Get sample content to extract data fields
        var sampleContent = await _session.Query<Content>()
            .Where(c => c.ContentType == contentType)
            .Take(1)
            .FirstOrDefaultAsync(ct);

        if (sampleContent != null)
        {
            collection.DataFields = ExtractDataFields(sampleContent);
        }

        return collection;
    }

    public string ResolveVariables(string template, Content content)
    {
        ArgumentNullException.ThrowIfNull(content, nameof(content));

        if (string.IsNullOrEmpty(template))
            return template;

        var sb = new System.Text.StringBuilder(template);

        // Resolve system variables
        sb.Replace("{{id}}", content.Id.ToString());
        sb.Replace("{{contentType}}", content.ContentType ?? string.Empty);
        sb.Replace("{{status}}", content.Status.ToString());
        sb.Replace("{{createdAt}}", content.CreatedAt.ToString("o"));
        sb.Replace("{{updatedAt}}", content.UpdatedAt.ToString("o"));

        // Resolve data fields
        if (content.Data != null)
        {
            foreach (var kvp in content.Data)
            {
                var variableName = $"{{{{data.{kvp.Key}}}}}";
                var value = kvp.Value?.ToString() ?? "";
                sb.Replace(variableName, value);
            }
        }

        return sb.ToString();
    }

    private List<TemplateVariable> GetSystemVariables()
    {
        return new List<TemplateVariable>
        {
            new()
            {
                Name = "{{id}}",
                Description = "Content unique identifier",
                Example = "3fa85f64-5717-4562-b3fc-2c963f66afa6",
                Type = "string"
            },
            new()
            {
                Name = "{{contentType}}",
                Description = "Content type name",
                Example = "PurchaseOrder",
                Type = "string"
            },
            new()
            {
                Name = "{{status}}",
                Description = "Content status",
                Example = "Published",
                Type = "string"
            },
            new()
            {
                Name = "{{createdAt}}",
                Description = "When the content was created",
                Example = "2024-12-16T10:00:00Z",
                Type = "datetime"
            },
            new()
            {
                Name = "{{updatedAt}}",
                Description = "When the content was last updated",
                Example = "2024-12-16T15:30:00Z",
                Type = "datetime"
            }
        };
    }

    private List<TemplateVariable> ExtractDataFields(Content content)
    {
        var fields = new List<TemplateVariable>();

        foreach (var kvp in content.Data)
        {
            var value = kvp.Value;
            var type = "string";

            if (value != null)
            {
                if (int.TryParse(value.ToString(), out _) || decimal.TryParse(value.ToString(), out _))
                {
                    type = "number";
                }
                else if (bool.TryParse(value.ToString(), out _))
                {
                    type = "boolean";
                }
                else if (DateTime.TryParse(value.ToString(), out _))
                {
                    type = "datetime";
                }
            }

            fields.Add(new TemplateVariable
            {
                Name = $"{{{{data.{kvp.Key}}}}}",
                Description = $"Content data field: {kvp.Key}",
                Example = value?.ToString() ?? "",
                Type = type
            });
        }

        return fields;
    }
}
