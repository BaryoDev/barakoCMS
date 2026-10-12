using barakoCMS.Core.Hooks;
using barakoCMS.Core.Interfaces;
using barakoCMS.Models;
using Marten;
using Microsoft.Extensions.Options;

namespace BarakoCMS.Pages;

/// <summary>
/// The rules a page tree needs that a schema cannot state: a page is not its own ancestor, is not
/// deeper than <see cref="PagesOptions.MaxDepth"/>, and a root page does not take a reserved slug.
/// Read for the tenant writing, so the type and fields are the ones its <see cref="PagesSettings"/>
/// name, or <c>Modules:Pages</c> where it names none.
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
    private readonly PagesOptions _deployment;

    /// <remarks>
    /// <paramref name="projector"/> is no longer read: the slug check on write uses the authoring
    /// rule. It stays so this public constructor keeps its signature.
    /// </remarks>
    public PageTreeHook(IOptions<PagesOptions> options, IPublicContentProjector projector)
    {
        _deployment = options.Value;
        _ = projector;
    }

    /// <summary>
    /// Every type, <see cref="IContentLifecycleHook.AnyType"/>: which type is the page tree is the
    /// tenant's to say (<see cref="PagesSettings"/>), so the hook reads it per write and lets any
    /// other type through.
    /// </summary>
    public string ContentType => IContentLifecycleHook.AnyType;

    public async Task<IReadOnlyList<string>> OnBeforeSaveAsync(ContentLifecycleContext context, CancellationToken ct)
    {
        var options = await PagesSettings.ResolveAsync(context.Session, _deployment, ct);
        if (!string.Equals(context.ContentType, options.ContentType, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var errors = new List<string>();
        var definition = await DefinitionAsync(context, options, ct);

        if (PageData.Guid(context.Data, options.ParentField) is null)
        {
            if (ReservedRootSlug(context, options, definition) is { } reserved)
            {
                errors.Add($"The slug '{reserved}' is reserved and cannot be used by a top-level page.");
            }

            return errors;
        }

        // The type declares this field its parent, as tight as this tree allows or tighter, so core
        // walks the chain on every write to it and a second walk would only repeat its errors. A
        // declaration core would not walk, or one deeper than MaxDepth, leaves the walk here.
        if (definition?.Fields.Any(f => f.Parent is { } parent
                && parent.MaxDepth <= options.MaxDepth
                && string.Equals(f.Name, options.ParentField, StringComparison.OrdinalIgnoreCase)
                && string.Equals(f.Type, "reference", StringComparison.OrdinalIgnoreCase)
                && !f.Multiple
                && string.Equals(f.ReferenceType, definition.Name, StringComparison.OrdinalIgnoreCase)) == true)
        {
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

        var chain = new ParentReferenceHook(options.ContentType, options.ParentField, options.MaxDepth);
        errors.AddRange(await chain.OnBeforeSaveAsync(walked, ct));
        return errors;
    }

    private static Task<ContentTypeDefinition?> DefinitionAsync(
        ContentLifecycleContext context, PagesOptions options, CancellationToken ct)
    {
        var lowered = options.ContentType.ToLower();
        return context.Session.Query<ContentTypeDefinition>()
            .FirstOrDefaultAsync(d => d.Name.ToLower() == lowered, ct);
    }

    private static string? ReservedRootSlug(
        ContentLifecycleContext context, PagesOptions options, ContentTypeDefinition? definition)
    {
        if (options.ReservedSlugs.Count == 0
            || AuthoringSlugField(definition) is not { } slugField
            || PageData.String(context.Data, slugField) is not { Length: > 0 } slug)
        {
            return null;
        }

        return options.ReservedSlugs.Any(r => string.Equals(r?.Trim(), slug.Trim(), StringComparison.OrdinalIgnoreCase))
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
