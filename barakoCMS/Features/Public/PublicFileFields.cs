using barakoCMS.Core.Interfaces;
using barakoCMS.Core.Validation;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace barakoCMS.Features.Public;

/// <summary>
/// Replaces the id in each <c>file</c> field of a delivered entry with the file it names.
/// </summary>
/// <remarks>
/// Run on entries that <see cref="PublicDelivery.ToPublic"/> has already projected, so a file field
/// the type does not mark Public is gone before this looks.
///
/// Only a public file resolves. That is the rule the Files module's own anonymous routes apply,
/// and the one a workflow's email attachments follow: work done for no signed-in caller reads
/// public files and nothing else. A field naming any other file is left out of the entry: a
/// private file, a deleted one, an id that was never a file, and every file on a host with no
/// module that stores them. Left out, not answered as an id, so an entry does not say that a file
/// exists which the reader may not have.
/// </remarks>
internal static class PublicFileFields
{
    public static Task<List<PublicContentResponse>> ResolveAsync(
        IReadOnlyList<PublicContentResponse> items,
        ContentTypeDefinition definition,
        IFileStore? files,
        CancellationToken ct) =>
        ResolveAsync(items.Select(item => (Item: item, Definition: definition)).ToList(), files, ct);

    /// <summary>The same entries in the same order, each read against its own type.</summary>
    public static async Task<List<PublicContentResponse>> ResolveAsync(
        IReadOnlyList<(PublicContentResponse Item, ContentTypeDefinition Definition)> items,
        IFileStore? files,
        CancellationToken ct)
    {
        var fieldsOf = new Dictionary<ContentTypeDefinition, HashSet<string>>(ReferenceEqualityComparer.Instance);
        var ids = new HashSet<Guid>();

        foreach (var (item, definition) in items)
        {
            if (!fieldsOf.TryGetValue(definition, out var fields))
                fieldsOf[definition] = fields = FileFields.Names(definition);

            FileFieldResolver.Collect(item.Data, fields, ids);
        }

        var found = await FileFieldResolver.ReadAsync(files, ids, caller: null, ct);

        return items.Select(pair => Resolve(pair.Item, fieldsOf[pair.Definition], found)).ToList();
    }

    private static readonly IReadOnlyDictionary<Guid, StoredFileInfo> NoFiles = new Dictionary<Guid, StoredFileInfo>();

    /// <summary>
    /// The entry with every file field left out, and the SEO image of a file field cleared, for a
    /// payload that does not read the file store: the event stream and the module projector's
    /// synchronous member.
    /// </summary>
    /// <remarks>
    /// What delivery answers for a file it may not show, so these payloads never hold an id the
    /// routes that resolve files would have left out.
    /// </remarks>
    public static PublicContentResponse LeaveOut(PublicContentResponse item, ContentTypeDefinition definition) =>
        Resolve(item, FileFields.Names(definition), NoFiles);

    /// <summary>The data with every file field holding a value left out, for a webhook payload.</summary>
    public static Dictionary<string, object> LeaveOut(Dictionary<string, object> data, ContentTypeDefinition? definition)
    {
        var fields = FileFields.Names(definition);
        if (fields.Count == 0)
            return data;

        return data
            .Where(kv => kv.Value is null || !fields.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static PublicContentResponse Resolve(
        PublicContentResponse item, HashSet<string> fields, IReadOnlyDictionary<Guid, StoredFileInfo> found)
    {
        if (fields.Count == 0 || !item.Data.Keys.Any(fields.Contains))
            return item;

        var data = new Dictionary<string, object>(item.Data.Count);
        string? socialImage = null;

        foreach (var (key, value) in item.Data)
        {
            if (!fields.Contains(key) || value is null)
            {
                data[key] = value!;
                continue;
            }

            // A public file whose store gives no address has nothing a reader could fetch.
            if (FileFields.TryReadId(value, out var id)
                && found.TryGetValue(id, out var file)
                && !string.IsNullOrEmpty(file.PublicUrl))
            {
                data[key] = ResolvedFile.From(file);

                if (string.Equals(key, barakoCMS.Features.Seo.SeoFields.SocialImage, StringComparison.OrdinalIgnoreCase))
                    socialImage = file.PublicUrl;
            }
        }

        // The SEO block was read off the stored data, where a file field holds an id. An id is not
        // an image address, so the block takes the file's own, or nothing.
        var seo = item.Seo is not null && fields.Contains(barakoCMS.Features.Seo.SeoFields.SocialImage)
            ? item.Seo with { ImageUrl = socialImage }
            : item.Seo;

        return item with { Data = data, Seo = seo };
    }
}
