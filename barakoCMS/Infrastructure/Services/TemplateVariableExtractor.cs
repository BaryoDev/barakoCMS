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

    /// <summary>
    /// What the resolves since the last prepare left out on purpose, for the run to record: a loop
    /// that stopped at its cap, with how many references the field held.
    /// </summary>
    /// <remarks>The default has nothing to say, for an implementation written before this existed.</remarks>
    IReadOnlyList<string> Notes => [];
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
/// <remarks>
/// Built with only a session, it follows no reference and builds no link, and those placeholders
/// are left as written.
/// </remarks>
public class TemplateVariableExtractor(
    IDocumentSession session,
    IPermissionResolver? permissions,
    barakoCMS.Core.Interfaces.ISensitivityService? sensitivity,
    Microsoft.Extensions.Configuration.IConfiguration? configuration) : ITemplateVariableExtractor
{
    public TemplateVariableExtractor(IDocumentSession session)
        : this(session, null, null, null)
    {
    }

    /// <summary>The setting that holds the console's URL, which <c>links.console</c> is built on.</summary>
    public const string ConsoleUrlKey = "App:ConsoleUrl";

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

        var list = templates as IReadOnlyCollection<string> ?? templates.ToList();
        var needs = TemplateExpression.Needs(list);
        var (zone, currency, siteUrl) = needs.Site ? await SiteSettingsAsync(ct) : NoSiteSettings;
        _fired = null;

        _prepared = new TemplateContext(
            zone,
            currency,
            needs.Author ? await PersonAsync(content.CreatedBy, ct) : null,
            needs.Transition ? await TransitionAsync(content, triggerEvent, eventSequence, ct) : null,
            LinksWith(siteUrl),
            await ReferencesAsync(content, triggerEvent, eventSequence, TemplateExpression.FollowedFields(list), ct),
            []);
        _preparedFor = content.Id;
    }

    /// <remarks>A simulation follows no reference: it has no user to read them as.</remarks>
    public async Task PrepareSampleAsync(
        Content content, string? triggerEvent, IEnumerable<string> templates, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content, nameof(content));

        var (zone, currency, siteUrl) = TemplateExpression.Needs(templates).Site ? await SiteSettingsAsync(ct) : NoSiteSettings;
        var transition = triggerEvent is null ? null : WorkflowEvents.TransitionName(triggerEvent);

        _prepared = new TemplateContext(
            zone,
            currency,
            TemplatePerson.Sample,
            transition is { Length: > 0 } ? new TemplateTransition(transition, content.UpdatedAt, TemplatePerson.Sample) : null,
            LinksWith(siteUrl),
            References: null,
            []);
        _preparedFor = content.Id;
    }

    public IReadOnlyList<string> Notes => _prepared.Notes ?? [];

    private static readonly (TimeZoneInfo? Zone, string? Currency, string? Url) NoSiteSettings = (TimeZoneInfo.Utc, null, null);

    /// <summary>The bases links are built on, or null when this extractor was given no configuration.</summary>
    private TemplateLinks? LinksWith(string? siteUrl) =>
        configuration is null
            ? null
            : new TemplateLinks(
                AbsoluteBase(configuration[barakoCMS.Infrastructure.Security.CanonicalHost.BaseUrlKey]),
                AbsoluteBase(configuration[ConsoleUrlKey]),
                AbsoluteBase(siteUrl));

    /// <summary>An absolute http or https URL with no trailing slash, or null for anything else.</summary>
    /// <remarks>
    /// Null and not an error: a link that cannot be built is left out of the message, and a setting
    /// that is not a URL must not stop the message going.
    /// </remarks>
    private static string? AbsoluteBase(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return null;
        }

        return text.TrimEnd('/');
    }

    /// <summary>The time zone, currency and URL of the tenant's published <c>site</c> entry.</summary>
    /// <remarks>
    /// No entry, or no <c>TimeZone</c> on it, is UTC. A <c>TimeZone</c> this server does not know is
    /// null, which leaves every date format as written: a typing mistake in the setting should show
    /// in the first message sent, and UTC in its place would be hours wrong and look right.
    /// </remarks>
    private async Task<(TimeZoneInfo? Zone, string? Currency, string? Url)> SiteSettingsAsync(CancellationToken ct)
    {
        var site = await session.Query<Content>()
            .Where(c => c.ContentType == barakoCMS.Features.Site.ShareLinks.ShareLinkKeys.SiteType
                        && c.Status == ContentStatus.Published)
            .OrderBy(c => c.CreatedAt)
            .Take(1)
            .FirstOrDefaultAsync(ct);

        var zone = Setting(site, "TimeZone");

        return (zone is null ? TimeZoneInfo.Utc : TemplateExpression.Zone(zone),
            TemplateExpression.CurrencyCode(Setting(site, "Currency")),
            Setting(site, "Url"));
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

    private bool? _tenantIsRegistered;

    /// <summary>The user as a template may name them, or nobody.</summary>
    /// <remarks>
    /// A user is global and a workflow belongs to one tenant, so the id on an entry is not enough to
    /// hand out a name and an address: a platform administrator working under a tenant header, a
    /// member since removed and the owner of an API key all leave their id on what they touch. In a
    /// registered tenant only an active member is named. Anyone else is nobody, like a deleted account.
    /// </remarks>
    private async Task<TemplatePerson> PersonAsync(Guid userId, CancellationToken ct)
    {
        if (userId == Guid.Empty || !await IsNameableAsync(userId, ct)) return TemplatePerson.Nobody;

        var user = await session.LoadAsync<User>(userId, ct);
        return user is null ? TemplatePerson.Nobody : new TemplatePerson(user.Username, user.Email);
    }

    /// <summary>
    /// The rule sign-in applies in <c>TokenIssuer</c>: the default tenant and a slug nobody
    /// registered have no memberships to ask, and every user of the deployment works there. A
    /// registered tenant asks for an active membership.
    /// </summary>
    private async Task<bool> IsNameableAsync(Guid userId, CancellationToken ct)
    {
        var slug = barakoCMS.Infrastructure.Multitenancy.TenantScopes.SlugFor(session.TenantId);
        if (slug == Tenant.DefaultSlug) return true;

        _tenantIsRegistered ??= await session.Query<Tenant>().AnyAsync(t => t.Slug == slug, ct);
        if (_tenantIsRegistered == false) return true;

        return await session.Query<Membership>()
            .AnyAsync(m => m.UserId == userId && m.TenantSlug == slug && m.Status == MembershipStatus.Active, ct);
    }

    /// <summary>The transition event that fired the workflow, or null when there is none to name.</summary>
    /// <remarks>
    /// Read from the event and not from the entry. By the time an action runs the entry may have
    /// been edited again, and its last editor and time are then somebody else's.
    ///
    /// With a sequence it is that one event, read by its sequence and checked to be on this entry's
    /// stream. Not the stream itself: every edit on it carries the entry's whole data, and an entry
    /// edited thousands of times would be read in full to find one event.
    ///
    /// Without a sequence, which is the engine called directly by a host or a test, it is the
    /// latest event of that transition on the entry. Two transitions of one name before the first
    /// is handled would both name the later one. An event stored before events carried their own
    /// time gives the entry's last change as the time.
    /// </remarks>
    private async Task<TemplateTransition?> TransitionAsync(
        Content content, string? triggerEvent, long eventSequence, CancellationToken ct)
    {
        if (triggerEvent is null || WorkflowEvents.TransitionName(triggerEvent) is not { Length: > 0 } name)
        {
            return null;
        }

        if (eventSequence > 0)
        {
            var fired = await FiredAsync(eventSequence, ct);

            if (fired is null || fired.StreamId != content.Id || fired.Data is not barakoCMS.Events.ContentTransitioned at)
            {
                return null;
            }

            return new TemplateTransition(
                at.Transition, ContentProjection.OccurredAt(fired), await PersonAsync(at.UpdatedBy, ct));
        }

        var contentId = content.Id;
        var latest = await session.Events.QueryRawEventDataOnly<barakoCMS.Events.ContentTransitioned>()
            .Where(e => e.Id == contentId && e.Transition == name)
            .OrderByDescending(e => e.OccurredAt)
            .FirstOrDefaultAsync(ct);

        if (latest is null)
        {
            return null;
        }

        return new TemplateTransition(
            latest.Transition,
            latest.OccurredAt == default ? content.UpdatedAt : latest.OccurredAt,
            await PersonAsync(latest.UpdatedBy, ct));
    }

    private (long Sequence, JasperFx.Events.IEvent? Event)? _fired;

    /// <summary>The event of a sequence, read once for one prepare however many placeholders ask.</summary>
    private async Task<JasperFx.Events.IEvent?> FiredAsync(long sequence, CancellationToken ct)
    {
        if (_fired is { } read && read.Sequence == sequence) return read.Event;

        var fired = await session.Events.QueryAllRawEvents()
            .Where(e => e.Sequence == sequence)
            .FirstOrDefaultAsync(ct);

        _fired = (sequence, fired);
        return fired;
    }

    /// <summary>
    /// The user whose action fired the workflow, whose read permission decides what a followed
    /// reference renders. Empty when there is none, and then every reference renders empty.
    /// </summary>
    /// <remarks>
    /// With a sequence it is the user on that event, so a later edit by somebody else does not
    /// change whose permission is asked. An event on another entry's stream, or one that carries no
    /// user, is nobody. Without a sequence, which is the engine called in line with the change, it
    /// is the entry's author for a create and its last editor for anything else.
    /// </remarks>
    private async Task<Guid> ActorAsync(Content content, string? triggerEvent, long eventSequence, CancellationToken ct)
    {
        if (eventSequence <= 0)
        {
            return triggerEvent == WorkflowEvents.Created ? content.CreatedBy : content.LastModifiedBy;
        }

        var fired = await FiredAsync(eventSequence, ct);
        if (fired is null || fired.StreamId != content.Id) return Guid.Empty;

        return fired.Data switch
        {
            barakoCMS.Events.ContentCreated e => e.CreatedBy,
            barakoCMS.Events.ContentUpdated e => e.UpdatedBy,
            barakoCMS.Events.ContentStatusChanged e => e.UpdatedBy,
            barakoCMS.Events.ContentTransitioned e => e.UpdatedBy,
            barakoCMS.Events.ContentScheduled e => e.UpdatedBy,
            barakoCMS.Events.ContentSensitivityScheduled e => e.UpdatedBy,
            barakoCMS.Events.ContentSensitivityChanged e => e.UpdatedBy,
            _ => Guid.Empty,
        };
    }

    /// <summary>
    /// The reference fields the templates follow, each with the entries it points at as the
    /// triggering user may read them. Null when the templates follow none, or when this extractor
    /// was given nothing to check a read with.
    /// </summary>
    /// <remarks>
    /// One level deep: a loop item's own references are not followed. The reads are bounded and do
    /// not grow with the number of placeholders: the entry's type, the user, and one query for every
    /// id the named fields hold, at most <see cref="TemplateExpression.MaxLoopItems"/> of a list.
    /// Each entry is then checked with the permission and sensitivity services the API reads with,
    /// which cache the user's roles and each type's schema for the scope.
    ///
    /// An entry the user may not read is left out, the same as one that does not exist, and a field
    /// of a readable entry is kept only when the read would show it unchanged: a masked value, even
    /// one showing its last four characters, is not a value this user sees.
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, TemplateFollowed>?> ReferencesAsync(
        Content content, string? triggerEvent, long eventSequence, HashSet<string> named, CancellationToken ct)
    {
        if (named.Count == 0 || permissions is null || sensitivity is null || content.Data is null) return null;

        var followed = new Dictionary<string, TemplateFollowed>(StringComparer.Ordinal);
        if (content is barakoCMS.Features.Workflows.ErasedContent) return followed;

        var type = content.ContentType;
        var definition = await session.Query<ContentTypeDefinition>().FirstOrDefaultAsync(d => d.Name == type, ct);

        var fields = definition?.Fields
            .Where(f => f is not null
                        && named.Contains(f.Name)
                        && string.Equals(f.Type, "reference", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];

        if (fields.Count == 0) return null;

        var idsOf = new Dictionary<string, (List<Guid> Ids, int Total)>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            idsOf[field.Name] = IdsIn(content.Data.TryGetValue(field.Name, out var value) ? value : null, field.Multiple);
        }

        var readable = await ReadableAsync(
            idsOf.Values.SelectMany(v => v.Ids).Distinct().ToArray(),
            await ActorAsync(content, triggerEvent, eventSequence, ct),
            ct);

        foreach (var field in fields)
        {
            var (ids, total) = idsOf[field.Name];
            var items = new List<Content>(ids.Count);
            foreach (var id in ids)
            {
                if (readable.TryGetValue(id, out var item)
                    && (string.IsNullOrEmpty(field.ReferenceType)
                        || string.Equals(item.Type, field.ReferenceType, StringComparison.OrdinalIgnoreCase)))
                {
                    items.Add(item.Shown);
                }
            }

            followed[field.Name] = new TemplateFollowed(field.Multiple, total, items);
        }

        return followed;
    }

    /// <summary>The ids a reference value holds, the first <see cref="TemplateExpression.MaxLoopItems"/> of a list, and how many it holds.</summary>
    private static (List<Guid> Ids, int Total) IdsIn(object? value, bool multiple)
    {
        if (value is null) return ([], 0);

        if (multiple)
        {
            if (!barakoCMS.Core.Validation.ReferenceFields.TryReadList(value, out var texts)) return ([], 0);

            var ids = texts.Select(t => Guid.TryParse(t, out var id) ? id : Guid.Empty).ToList();
            return (ids.Take(TemplateExpression.MaxLoopItems).Where(id => id != Guid.Empty).ToList(), ids.Count);
        }

        var text = value is System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } element
            ? element.GetString()
            : value.ToString();

        return Guid.TryParse(text, out var single) ? ([single], 1) : ([], 0);
    }

    /// <summary>
    /// The entries among <paramref name="ids"/> the user may read, each holding only the fields the
    /// user is shown, by id with the entry's stored type. One query for the entries.
    /// </summary>
    /// <remarks>
    /// A document the read answers as hidden is shown as <c>GET /api/contents/{id}</c> shows it:
    /// no data, and <c>HIDDEN</c> as its content type. Its stored type is kept beside it only to
    /// check it is the type the reference field points at.
    /// </remarks>
    private async Task<Dictionary<Guid, (string Type, FollowedContent Shown)>> ReadableAsync(Guid[] ids, Guid actor, CancellationToken ct)
    {
        var readable = new Dictionary<Guid, (string Type, FollowedContent Shown)>();
        if (ids.Length == 0 || actor == Guid.Empty) return readable;

        var user = await session.LoadAsync<User>(actor, ct);
        if (user is null) return readable;

        var entries = await session.Query<Content>().Where(c => c.Id.In(ids)).ToListAsync(ct);

        var request = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim("UserId", user.Id.ToString())], nameof(TemplateVariableExtractor))),
        };

        foreach (var entry in entries)
        {
            if (!await permissions!.CanPerformActionAsync(user, entry.ContentType, "read", entry, ct)) continue;

            var shown = new Dictionary<string, object>(entry.Data);
            var hidden = await sensitivity!.ApplyAsync(entry, shown, request, ct);

            var data = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var (key, value) in shown)
            {
                if (!hidden && entry.Data.TryGetValue(key, out var stored) && ReferenceEquals(stored, value)) data[key] = value;
            }

            readable[entry.Id] = (entry.ContentType, new FollowedContent
            {
                Id = entry.Id,
                ContentType = hidden ? "HIDDEN" : entry.ContentType,
                Status = entry.Status,
                Sensitivity = entry.Sensitivity,
                CreatedAt = entry.CreatedAt,
                UpdatedAt = entry.UpdatedAt,
                Data = data,
            });
        }

        return readable;
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

        // A backstop: resolving is linear, and this bounds what one parameter costs on every run.
        if (template.Length > TemplateExpression.MaxTemplateLength)
        {
            context.Note(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"A parameter of {template.Length} characters is past the {TemplateExpression.MaxTemplateLength} character cap, so its placeholders were not resolved and it was sent as written."));
            return template;
        }

        // Single pass over the ORIGINAL template. Because each {{...}} token is resolved exactly
        // once and substituted values are NOT re-scanned, a content field whose value itself
        // contains "{{data.Other}}" cannot inject/leak another field (second-order injection).
        // A formatted value, a name, a duration, a link and a referenced entry's value go through
        // Encode like any other value. A loop's body is template text, resolved once per item.
        var rendered = new System.Text.StringBuilder(template.Length);
        foreach (var piece in TemplateExpression.Pieces(template))
        {
            switch (piece.Kind)
            {
                case TemplateExpression.PieceKind.Hole:
                    var value = TemplateExpression.Evaluate(piece.Hole!.Groups[1].Value, content, context);
                    rendered.Append(value is null ? piece.Hole.Value : Encode(value, encoding));
                    break;

                case TemplateExpression.PieceKind.Loop when TemplateExpression.LoopItems(piece, context) is { } items:
                    var itemContext = context.ForItem();
                    foreach (var item in items)
                    {
                        rendered.Append(Resolve(piece.Body!, item, encoding, itemContext));
                    }

                    break;

                default:
                    rendered.Append(template, piece.Start, piece.Length);
                    break;
            }
        }

        return rendered.ToString();
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
            },
            new()
            {
                Name = "{{links.console}}",
                Description = "The entry in the console, from App:ConsoleUrl. Empty when that is not set",
                Example = "https://console.example.com/content/3fa85f64-5717-4562-b3fc-2c963f66afa6",
                Type = "string"
            },
            new()
            {
                Name = "{{links.edit}}",
                Description = "The same as links.console: the console's page for the entry, where it is edited",
                Example = "https://console.example.com/content/3fa85f64-5717-4562-b3fc-2c963f66afa6",
                Type = "string"
            },
            new()
            {
                Name = "{{links.entry}}",
                Description = "The entry in this API, from App:BaseUrl. Empty when that is not set",
                Example = "https://api.example.com/api/contents/3fa85f64-5717-4562-b3fc-2c963f66afa6",
                Type = "string"
            },
            new()
            {
                Name = "{{links.site}}",
                Description = "The Url of the site settings. Empty when there is none",
                Example = "https://example.com",
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
        new()
        {
            Name = "{{links.site \"/approvals/\"}}",
            Description = "A page of the site: the Url of the site settings followed by the path, which starts with one /",
            Example = "https://example.com/approvals/",
            Type = "string"
        },
        new()
        {
            Name = "{{links.transition \"Approve\"}}",
            Description = "The entry in the console with the transition named ready to confirm. The approver signs in, and the transition's own permission decides",
            Example = "https://console.example.com/content/3fa85f64-5717-4562-b3fc-2c963f66afa6?transition=Approve",
            Type = "string"
        },
        new()
        {
            Name = "{{data.Reference.Field}}",
            Description = "A field of the entry a reference field points at, when the user who fired the workflow may read it. Empty when they may not",
            Example = "text",
            Type = "string"
        },
        new()
        {
            Name = "{{#each data.References}}{{data.Field}} {{/each}}",
            Description = $"The text between the markers once for each entry a reference field points at that the user who fired the workflow may read, at most {TemplateExpression.MaxLoopItems}",
            Example = "text text ",
            Type = "string"
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
