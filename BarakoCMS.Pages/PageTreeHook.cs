using barakoCMS.Core.Hooks;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Pages;

/// <summary>
/// The rules a page tree needs that a schema cannot state: a page is not its own ancestor, is not
/// deeper than <see cref="PagesOptions.MaxDepth"/>, and a root page does not take a reserved slug.
/// </summary>
/// <remarks>
/// <para>
/// The parent chain is checked by <see cref="ParentReferenceHook"/>, which walks it inside the write's
/// transaction under an advisory lock, so two concurrent saves cannot close a loop between them.
/// </para>
/// <para>
/// That hook skips a create, because a new entry cannot be part of a loop. It can still be too deep,
/// or be hung under a loop some other write path stored, so a create is walked too, under an id that
/// was minted for the walk and so cannot appear anywhere in the tree.
/// </para>
/// <para>
/// Two siblings with the same slug are not checked here. Core refuses a slug another entry of the
/// type already holds (#717), which covers siblings and is stricter.
/// </para>
/// </remarks>
public sealed class PageTreeHook : IContentLifecycleHook
{
    private readonly PagesOptions _options;

    /// <remarks>
    /// <paramref name="projector"/> is no longer read: the slug check on write uses the authoring
    /// rule. It stays so this public constructor keeps its signature.
    /// </remarks>
    public PageTreeHook(IOptions<PagesOptions> options, IPublicContentProjector projector)
    {
        _options = options.Value;
        _ = projector;
    }

    public string ContentType => _options.ContentType;

    public async Task<IReadOnlyList<string>> OnBeforeSaveAsync(ContentLifecycleContext context, CancellationToken ct)
    {
        var errors = new List<string>();

        if (PageData.Guid(context.Data, _options.ParentField) is null)
        {
            if (await ReservedRootSlugAsync(context, ct) is { } reserved)
            {
                errors.Add($"The slug '{reserved}' is reserved and cannot be used by a top-level page.");
            }

            return errors;
        }

        var walked = context.EntryId is null
            ? new ContentLifecycleContext
            {
                ContentType = context.ContentType,
                Data = context.Data,
                Existing = context.Existing,
                EntryId = Guid.NewGuid(),
                Session = context.Session,
                UserId = context.UserId,
            }
            : context;

        var chain = new ParentReferenceHook(_options.ContentType, _options.ParentField, _options.MaxDepth);
        errors.AddRange(await chain.OnBeforeSaveAsync(walked, ct));
        return errors;
    }

    private async Task<string?> ReservedRootSlugAsync(ContentLifecycleContext context, CancellationToken ct)
    {
        if (_options.ReservedSlugs.Count == 0)
        {
            return null;
        }

        var lowered = _options.ContentType.ToLower();
        var definition = await context.Session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name.ToLower() == lowered, ct);

        if (AuthoringSlugField(definition) is not { } slugField
            || PageData.String(context.Data, slugField) is not { Length: > 0 } slug)
        {
            return null;
        }

        return _options.ReservedSlugs.Any(r => string.Equals(r?.Trim(), slug.Trim(), StringComparison.OrdinalIgnoreCase))
            ? slug
            : null;
    }

    /// <summary>
    /// The field a write treats as the slug: a field of type slug whatever its sensitivity, else a
    /// Public text field named slug. The rule core's authoring checks use.
    /// </summary>
    /// <remarks>
    /// Not <c>IPublicContentProjector.SlugField</c>, which answers null for a slug field that is not
    /// Public. A reserved slug is refused on write whether or not delivery serves the field today,
    /// so making the field Public later cannot put a page on a reserved path.
    /// </remarks>
    private static string? AuthoringSlugField(ContentTypeDefinition? definition)
    {
        if (definition is null)
        {
            return null;
        }

        var byType = definition.Fields.FirstOrDefault(f => string.Equals(f.Type, "slug", StringComparison.OrdinalIgnoreCase));
        if (byType is not null)
        {
            return byType.Name;
        }

        return definition.Fields.FirstOrDefault(f =>
            string.Equals(f.Name, "slug", StringComparison.OrdinalIgnoreCase)
            && f.Sensitivity == SensitivityLevel.Public
            && (string.Equals(f.Type, "string", StringComparison.OrdinalIgnoreCase)
                || string.Equals(f.Type, "text", StringComparison.OrdinalIgnoreCase)))?.Name;
    }
}
