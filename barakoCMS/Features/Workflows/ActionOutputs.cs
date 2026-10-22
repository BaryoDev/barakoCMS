using System.Globalization;
using System.Text.RegularExpressions;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace barakoCMS.Features.Workflows;

/// <summary>
/// What an action produced, what of it is kept, and how a later action reads it as
/// <c>{{actions.N.name}}</c>.
/// </summary>
/// <remarks>
/// Kept by allowlist: the workflow names each output an action may keep, and the rest is dropped
/// before the run is written. A provider's response is third-party data that can hold personal
/// data or a credential, and the run is stored and served over the API.
///
/// A reference is taken out of the template before the entry's placeholders are resolved and its
/// output is put back after, so an output that holds <c>{{createdBy.email}}</c> is sent as that
/// text, and so is an entry value that holds <c>{{actions.0.token}}</c>.
/// </remarks>
internal static partial class ActionOutputs
{
    /// <summary>How many outputs one action may keep.</summary>
    internal const int MaxCaptured = 20;

    /// <summary>The longest output name.</summary>
    internal const int MaxNameLength = 64;

    /// <summary>The longest value kept. A longer one is not kept, and the attempt says so.</summary>
    internal const int MaxValueLength = 2000;

    [GeneratedRegex("^[A-Za-z0-9_]{1,64}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^actions\.[0-9]{1,4}\.[A-Za-z0-9_]{1,64}$")]
    private static partial Regex ReferenceName();

    [GeneratedRegex(@"\{\{\s*actions\.(?<ordinal>[0-9]{1,4})\.(?<name>[A-Za-z0-9_]{1,64})\s*\}\}", RegexOptions.None, 2000)]
    private static partial Regex Reference();

    internal static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    /// <summary>Whether a placeholder's name reads an output, as in <c>actions.0.total</c>.</summary>
    internal static bool IsReference(string name) => ReferenceName().IsMatch(name);

    /// <summary>Every output a template reads, by position and name.</summary>
    internal static IEnumerable<(int Ordinal, string Name)> References(string? template)
    {
        if (string.IsNullOrEmpty(template))
        {
            yield break;
        }

        foreach (Match match in Reference().Matches(template))
        {
            yield return (int.Parse(match.Groups["ordinal"].Value, CultureInfo.InvariantCulture), match.Groups["name"].Value);
        }
    }

    /// <summary>The outputs a run's attempts kept, by position.</summary>
    internal static IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>> Kept(WorkflowRun run) =>
        run.Actions
            .Where(a => a.Status == AttemptStatus.Succeeded && a.Outputs is { Count: > 0 })
            .ToDictionary(a => a.Ordinal, a => (IReadOnlyDictionary<string, string>)a.Outputs!);

    /// <summary>The files a run's succeeded attempts declared they produced, by position.</summary>
    internal static IReadOnlyDictionary<int, IReadOnlyList<Guid>> KeptFiles(WorkflowRun run) =>
        run.Actions
            .Where(a => a.Status == AttemptStatus.Succeeded && a.ProducedFiles is { Count: > 0 })
            .ToDictionary(a => a.Ordinal, a => (IReadOnlyList<Guid>)a.ProducedFiles!);

    /// <summary>A note for an action that declared more files than a step keeps.</summary>
    internal static IEnumerable<string> DroppedFilesNote(int declared) =>
        declared <= WorkflowActionResult.MaxProducedFiles
            ? []
            : [$"The action declared {declared} files and only the first {WorkflowActionResult.MaxProducedFiles} were kept."];

    /// <summary>The outputs the capture list names, and a note for any it had to leave out.</summary>

    internal static (Dictionary<string, string>? Kept, string? Note) Keep(
        IReadOnlyDictionary<string, string>? produced, IReadOnlyCollection<string>? capture)
    {
        if (produced is not { Count: > 0 } || capture is not { Count: > 0 })
        {
            return (null, null);
        }

        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        var tooLong = new List<string>();

        foreach (var name in capture.Distinct(StringComparer.Ordinal).Take(MaxCaptured))
        {
            if (!produced.TryGetValue(name, out var value) || value is null)
            {
                continue;
            }

            if (value.Length > MaxValueLength)
            {
                tooLong.Add(name);
                continue;
            }

            kept[name] = value;
        }

        var note = tooLong.Count == 0
            ? null
            : $"Output(s) {string.Join(", ", tooLong)} were longer than {MaxValueLength} characters and were not kept.";

        return (kept.Count == 0 ? null : kept, note);
    }

    /// <summary>
    /// Swaps each <c>{{actions.N.name}}</c> written in the workflow for a token only this resolve
    /// knows, before the entry's placeholders are resolved.
    /// </summary>
    /// <remarks>
    /// A reference is read from the template and never from what resolving it produced, so an entry
    /// value holding <c>{{actions.0.token}}</c> is sent as that text and cannot pull in another
    /// action's output. The token is letters, digits and underscores, which no encoding changes, and
    /// carries a fresh random part, so an entry cannot write one either.
    /// </remarks>
    internal static (Dictionary<string, string> Masked, OutputSlots Slots) Mask(
        string actionType, IReadOnlyDictionary<string, string> parameters)
    {
        var slots = new OutputSlots(Guid.NewGuid().ToString("N"));
        var masked = new Dictionary<string, string>(parameters.Count);

        foreach (var (key, value) in parameters)
        {
            masked[key] = string.IsNullOrEmpty(value) || ActionParameters.IsResolvedByTheAction(actionType, key)
                ? value
                : Reference().Replace(value, match => slots.Add(
                    key,
                    int.Parse(match.Groups["ordinal"].Value, CultureInfo.InvariantCulture),
                    match.Groups["name"].Value));
        }

        return (masked, slots);
    }

    /// <summary>
    /// Puts the kept output in place of each token <see cref="Mask"/> left, encoded for where the
    /// parameter goes.
    /// </summary>
    /// <returns>Null when every reference was found, otherwise what is missing.</returns>
    /// <remarks>
    /// A parameter the action resolves itself is left alone, as the templates are: a Conditional's
    /// branches are JSON, and a value written into them could rewrite it.
    /// </remarks>
    internal static string? Substitute(
        string actionType, Dictionary<string, string> parameters, OutputSlots slots,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, string>> outputs)
    {
        string? missing = null;

        foreach (var (token, key, ordinal, name) in slots.All)
        {
            if (!parameters.TryGetValue(key, out var value) || string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (outputs.TryGetValue(ordinal, out var kept) && kept.TryGetValue(name, out var output))
            {
                parameters[key] = value.Replace(token, TemplateVariableExtractor.Encode(output, ActionParameters.EncodingFor(actionType, key)), StringComparison.Ordinal);
                continue;
            }

            missing ??= $"Parameter '{key}' reads {{{{actions.{ordinal}.{name}}}}}, and action {ordinal} has not "
                      + "succeeded with that output captured. It is tried again once it has.";
            parameters[key] = value.Replace(token, $"{{{{actions.{ordinal}.{name}}}}}", StringComparison.Ordinal);
        }

        return missing;
    }
}

/// <summary>The output references <see cref="ActionOutputs.Mask"/> took out of one action's parameters.</summary>
internal sealed class OutputSlots(string nonce)
{
    private readonly List<(string Token, string Key, int Ordinal, string Name)> _slots = [];

    internal IReadOnlyList<(string Token, string Key, int Ordinal, string Name)> All => _slots;

    internal string Add(string key, int ordinal, string name)
    {
        var token = $"__barako_output_{nonce}_{_slots.Count}__";
        _slots.Add((token, key, ordinal, name));
        return token;
    }
}
