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
    private readonly IPublicContentProjector _projector;

    public PageTreeHook(IOptions<PagesOptions> options, IPublicContentProjector projector)
    {
        _options = options.Value;
        _projector = projector;
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

        if (_projector.SlugField(definition) is not { } slugField
            || PageData.String(context.Data, slugField) is not { Length: > 0 } slug)
        {
            return null;
        }

        return _options.ReservedSlugs.Any(r => string.Equals(r?.Trim(), slug.Trim(), StringComparison.OrdinalIgnoreCase))
            ? slug
            : null;
    }
}
