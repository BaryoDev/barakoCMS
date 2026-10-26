using System.Text.RegularExpressions;
using barakoCMS.Infrastructure.Realtime;
using barakoCMS.Models;
using Marten;

namespace barakoCMS.Infrastructure.Services;

/// <summary>
/// What is refused about the realtime topic a content type declares: a name that is not written
/// the way topics are, a name a module owns, and a name another type already holds.
/// </summary>
/// <remarks>
/// Topic names are shared by every module and every content type of the tenant, so one name never
/// means two things to a client that subscribes by name.
/// </remarks>
public static partial class RealtimeTopicRules
{
    [GeneratedRegex("^[a-z0-9]+([.-][a-z0-9]+)*$")]
    private static partial Regex NamePattern();

    /// <summary>
    /// The refusal for the topic of <paramref name="type"/>, or null when it declares none or the
    /// name is free. The type's own stored topic is not a clash, except the reserved
    /// <c>workflow-runs</c>, which is refused even when the type already holds it.
    /// </summary>
    public static async Task<string?> RefusalAsync(
        ContentTypeDefinition type, TopicRegistry registry, IQuerySession session, CancellationToken ct)
    {
        if (type.RealtimeTopic is not { } topic)
        {
            return null;
        }

        if (!NamePattern().IsMatch(topic))
        {
            return $"Realtime topic '{Shorten(topic)}' must be lower case words joined by dots or dashes.";
        }

        if (string.Equals(topic, WorkflowRunTopic.Name, StringComparison.Ordinal))
        {
            return $"Realtime topic '{topic}' is reserved.";
        }

        if (registry.Find(topic) is { } owner)
        {
            return $"Realtime topic '{topic}' is used by module '{owner.Module}'.";
        }

        var holders = await session.Query<ContentTypeDefinition>().Where(d => d.RealtimeTopic == topic).ToListAsync(ct);
        var other = holders.FirstOrDefault(d => d.Id != type.Id && !SameName(d.Name, type.Name));
        return other is null ? null : $"Realtime topic '{topic}' is already used by content type '{other.Name}'.";
    }

    private static bool SameName(string stored, string given) =>
        barakoCMS.Core.ContentTypeName.Normalize(stored) == barakoCMS.Core.ContentTypeName.Normalize(given);

    private static string Shorten(string name) => name.Length > 100 ? name[..100] + "..." : name;
}
