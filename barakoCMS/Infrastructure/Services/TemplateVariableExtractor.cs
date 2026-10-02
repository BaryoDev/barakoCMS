using barakoCMS.Models;
using Marten;
using System.Text.RegularExpressions;

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

    /// <summary>
    /// Resolves the template like <see cref="ResolveVariables(string, Content)"/>, encoding each
    /// substituted value for where the result is going. The template's own text is never changed.
    /// </summary>
    /// <remarks>
    /// The default only covers <see cref="TemplateValueEncoding.None"/>. An implementation that
    /// cannot encode refuses rather than returning values unescaped into an HTML body.
    /// </remarks>
    string ResolveVariables(string template, Content content, TemplateValueEncoding encoding) =>
        encoding == TemplateValueEncoding.None
            ? ResolveVariables(template, content)
            : throw new NotSupportedException($"{GetType().Name} does not support {encoding} encoding.");

    /// <summary>
    /// Reads what one action's templates name beyond the entry, so the resolves that follow for the
    /// same entry can fill it: the site's time zone and currency, who created the entry, and the
    /// transition that fired the workflow.
    /// </summary>
    /// <param name="content">The entry the templates are resolved against.</param>
    /// <param name="triggerEvent">The trigger that fired, such as <c>transition:Approve</c>.</param>
    /// <param name="eventSequence">The sequence of the event that fired it, or zero when it is not known.</param>
    /// <param name="templates">The action's parameter values. Only what they name is read.</param>
    /// <param name="ct">Cancellation token for the reads.</param>
    /// <remarks>
    /// The default does nothing, so an implementation written before this existed still compiles and
    /// leaves the placeholders it does not know as written.
    /// </remarks>
    Task PrepareAsync(
        Content content, string? triggerEvent, long eventSequence, IEnumerable<string> templates, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <summary>
    /// The same for a simulation, which is handed an entry the caller wrote. No user and no event is
    /// read: the author and the transition are filled with sample values.
    /// </summary>
    Task PrepareSampleAsync(
        Content content, string? triggerEvent, IEnumerable<string> templates, CancellationToken ct = default) =>
        Task.CompletedTask;
}

/// <summary>How a substituted value is written into the text a template produces.</summary>
public enum TemplateValueEncoding
{
    /// <summary>As stored.</summary>
    None,

    /// <summary>HTML-encoded, for a value landing in an HTML body.</summary>
    Html,

    /// <summary>Line breaks replaced by a space, for a value landing in a header such as a subject.</summary>
    SingleLine,
}

/// <summary>
/// Extracts and documents available template variables for workflows.
/// </summary>
public class TemplateVariableExtractor(IDocumentSession session) : ITemplateVariableExtractor
{
    public async Task<TemplateVariableCollection> GetVariablesAsync(string contentType, CancellationToken ct = default)
    {
        var collection = new TemplateVariableCollection
        {
            SystemVariables = GetSystemVariables(),
            Formats = GetFormats()
        };

        // Get sample content to extract data fields
        var sampleContent = await session.Query<Content>()
            .Where(c => c.ContentType == contentType)
            .Take(1)
            .FirstOrDefaultAsync(ct);

        if (sampleContent != null)
        {
            collection.DataFields = ExtractDataFields(sampleContent);
        }

        return collection;
    }

    private static readonly Regex TemplateToken = TemplateExpression.Token;

    private Guid _preparedFor;
    private TemplateContext _prepared = TemplateContext.Unprepared;

    public string ResolveVariables(string template, Content content) =>
        Resolve(template, content, TemplateValueEncoding.None, PreparedFor(content));

    public string ResolveVariables(string template, Content content, TemplateValueEncoding encoding) =>
        Resolve(template, content, encoding, PreparedFor(content));

    /// <remarks>
    /// Kept on the extractor and not handed to each resolve, because a Conditional resolves its
    /// children with the extractor of the same scope and is told nothing but the entry. Kept per
    /// entry, so a resolve for some other entry never reads this one's author.
    /// </remarks>
    private TemplateContext PreparedFor(Content? content) =>
        content is not null && content.Id == _preparedFor ? _prepared : TemplateContext.Unprepared;

    public async Task PrepareAsync(
        Content content, string? triggerEvent, long eventSequence, IEnumerable<string> templates, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content, nameof(content));

        var needs = TemplateExpression.Needs(templates);
        var (zone, currency) = needs.Site ? await SiteSettingsAsync(ct) : NoSiteSettings;

        _prepared = new TemplateContext(
            zone,
            currency,
            needs.Author ? await PersonAsync(content.CreatedBy, ct) : null,
            needs.Transition ? await TransitionAsync(content, triggerEvent, eventSequence, ct) : null);
        _preparedFor = content.Id;
    }

    public async Task PrepareSampleAsync(
        Content content, string? triggerEvent, IEnumerable<string> templates, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content, nameof(content));

        var (zone, currency) = TemplateExpression.Needs(templates).Site ? await SiteSettingsAsync(ct) : NoSiteSettings;
        var transition = triggerEvent is null ? null : WorkflowEvents.TransitionName(triggerEvent);

        _prepared = new TemplateContext(
            zone,
            currency,
            TemplatePerson.Sample,
            transition is { Length: > 0 } ? new TemplateTransition(transition, content.UpdatedAt, TemplatePerson.Sample) : null);
        _preparedFor = content.Id;
    }

    private static readonly (TimeZoneInfo? Zone, string? Currency) NoSiteSettings = (TimeZoneInfo.Utc, null);

    /// <summary>The time zone and currency of the tenant's published <c>site</c> entry.</summary>
    /// <remarks>
    /// No entry, or no <c>TimeZone</c> on it, is UTC. A <c>TimeZone</c> this server does not know is
    /// null, which leaves every date format as written: a typing mistake in the setting should show
    /// in the first message sent, and UTC in its place would be hours wrong and look right.
    /// </remarks>
    private async Task<(TimeZoneInfo? Zone, string? Currency)> SiteSettingsAsync(CancellationToken ct)
    {
        var site = await session.Query<Content>()
            .Where(c => c.ContentType == barakoCMS.Features.Site.ShareLinks.ShareLinkKeys.SiteType
                        && c.Status == ContentStatus.Published)
            .OrderBy(c => c.CreatedAt)
            .Take(1)
            .FirstOrDefaultAsync(ct);

        var zone = Setting(site, "TimeZone");

        return (zone is null ? TimeZoneInfo.Utc : TemplateExpression.Zone(zone),
            TemplateExpression.CurrencyCode(Setting(site, "Currency")));
    }

    private static string? Setting(Content? site, string name)
    {
        if (site?.Data is null) return null;

        foreach (var (key, value) in site.Data)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                var text = value?.ToString()?.Trim();
                return string.IsNullOrEmpty(text) ? null : text;
            }
        }

        return null;
    }

    private async Task<TemplatePerson> PersonAsync(Guid userId, CancellationToken ct)
    {
        if (userId == Guid.Empty) return TemplatePerson.Nobody;

        var user = await session.LoadAsync<User>(userId, ct);
        return user is null ? TemplatePerson.Nobody : new TemplatePerson(user.Username, user.Email);
    }

    /// <summary>The transition event that fired the workflow, or null when there is none to name.</summary>
    /// <remarks>
    /// Read from the entry's stream and not from the entry. By the time an action runs the entry may
    /// have been edited again, and its last editor and time are then somebody else's.
    ///
    /// With a sequence it is that event and no other. Without one, which is the engine called
    /// directly, it is the last event of that transition on the entry.
    /// </remarks>
    private async Task<TemplateTransition?> TransitionAsync(
        Content content, string? triggerEvent, long eventSequence, CancellationToken ct)
    {
        if (triggerEvent is null || WorkflowEvents.TransitionName(triggerEvent) is not { Length: > 0 } name)
        {
            return null;
        }

        var stream = await session.Events.FetchStreamAsync(content.Id, token: ct);

        var fired = eventSequence > 0
            ? stream.FirstOrDefault(e => e.Sequence == eventSequence)
            : stream.LastOrDefault(e => e.Data is barakoCMS.Events.ContentTransitioned transitioned
                                        && string.Equals(transitioned.Transition, name, StringComparison.Ordinal));

        if (fired?.Data is not barakoCMS.Events.ContentTransitioned data)
        {
            return null;
        }

        return new TemplateTransition(
            data.Transition, ContentProjection.OccurredAt(fired), await PersonAsync(data.UpdatedBy, ct));
    }

    /// <summary>
    /// The resolution itself, which needs no database. Static so a caller holding no extractor, such
    /// as a conditional resolving its children, gets exactly the same rules.
    /// </summary>
    /// <remarks>
    /// There is no syntax for inserting a value raw into HTML. A data field can hold whatever a public
    /// form submitted, and the template cannot tell such a field from one an editor wrote.
    /// </remarks>
    public static string Resolve(string template, Content content, TemplateValueEncoding encoding) =>
        Resolve(template, content, encoding, TemplateContext.Unprepared);

    internal static string Resolve(string template, Content content, TemplateValueEncoding encoding, TemplateContext context)
    {
        ArgumentNullException.ThrowIfNull(content, nameof(content));

        if (string.IsNullOrEmpty(template))
            return template;

        // Single pass over the ORIGINAL template. Because each {{...}} token is resolved exactly
        // once and substituted values are NOT re-scanned, a content field whose value itself
        // contains "{{data.Other}}" cannot inject/leak another field (second-order injection).
        // A formatted value, a name and a duration go through Encode like any other value.
        return TemplateToken.Replace(template, match =>
        {
            var value = TemplateExpression.Evaluate(match.Groups[1].Value, content, context);
            return value is null ? match.Value : Encode(value, encoding);
        });
    }

    private static readonly Regex LineBreaks = new(@"[\r\n\u0085\u2028\u2029]+", RegexOptions.Compiled);

    private static string Encode(string value, TemplateValueEncoding encoding) => encoding switch
    {
        TemplateValueEncoding.Html => System.Net.WebUtility.HtmlEncode(value),
        TemplateValueEncoding.SingleLine => LineBreaks.Replace(value, " "),
        _ => value,
    };

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
            },
            new()
            {
                Name = "{{createdBy.name}}",
                Description = "Username of whoever created the content. Empty when no user did",
                Example = "maria",
                Type = "string"
            },
            new()
            {
                Name = "{{createdBy.email}}",
                Description = "Email address of whoever created the content. Empty when no user did",
                Example = "maria@example.com",
                Type = "string"
            },
            new()
            {
                Name = "{{transition.name}}",
                Description = "The transition that fired the workflow. Transition triggers only",
                Example = "Approve",
                Type = "string"
            },
            new()
            {
                Name = "{{transition.at}}",
                Description = "When the transition happened. Transition triggers only",
                Example = "2024-12-16T15:30:00Z",
                Type = "datetime"
            },
            new()
            {
                Name = "{{transition.by.name}}",
                Description = "Username of whoever made the transition. Transition triggers only",
                Example = "maria",
                Type = "string"
            },
            new()
            {
                Name = "{{transition.by.email}}",
                Description = "Email address of whoever made the transition. Transition triggers only",
                Example = "maria@example.com",
                Type = "string"
            }
        };
    }

    private static List<TemplateVariable> GetFormats() =>
    [
        new()
        {
            Name = "{{createdAt | date \"MMM d, h:mm tt\"}}",
            Description = "A date in the site's time zone, in a .NET date format. A second argument names another zone, such as \"Asia/Manila\"",
            Example = "Dec 16, 6:00 PM",
            Type = "string"
        },
        new()
        {
            Name = "{{data.Field | money}}",
            Description = "A number with two decimals, after the site's currency code. An argument names another code, such as \"USD\"",
            Example = "PHP 1,250.00",
            Type = "string"
        },
        new()
        {
            Name = "{{data.Field | upper}}",
            Description = "The value in capitals",
            Example = "TEXT",
            Type = "string"
        },
        new()
        {
            Name = "{{data.Field | lower}}",
            Description = "The value in small letters",
            Example = "text",
            Type = "string"
        },
        new()
        {
            Name = "{{duration createdAt updatedAt}}",
            Description = "The time between two dates, in hours and minutes",
            Example = "8 hours 30 minutes",
            Type = "string"
        },
        new()
        {
            Name = "{{hours createdAt updatedAt}}",
            Description = "The time between two dates, in hours to one decimal",
            Example = "8.5",
            Type = "number"
        },
    ];

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
                // A placeholder, not the stored value. Someone writing a workflow template needs
                // the field's name and type; the real contents of a record they may not be allowed
                // to read are not part of that. This path applies no sensitivity masking, so
                // echoing the value here handed a field marked Sensitive or Hidden straight back
                // in plaintext.
                Example = PlaceholderFor(type),
                Type = type
            });
        }

        return fields;
    }

    private static string PlaceholderFor(string type) => type switch
    {
        "number" => "123",
        "boolean" => "true",
        "datetime" => "2024-12-16T10:00:00Z",
        _ => "text",
    };
}
