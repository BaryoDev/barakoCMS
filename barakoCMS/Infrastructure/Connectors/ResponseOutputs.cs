using System.Globalization;
using System.Text.Json;
using barakoCMS.Models;

namespace barakoCMS.Infrastructure.Connectors;

/// <summary>One step of an output path: a property name, or an array index.</summary>
internal readonly record struct OutputPathSegment(string? Name, int? Index);

/// <summary>
/// The path an operator writes to say where in a response an output lives, such as
/// <c>$.choices[0].message.content</c>.
/// </summary>
/// <remarks>
/// A plain path and nothing more: property names and array indexes, each step reaching at most one
/// value. No wildcard, recursive descent, slice or filter, so a path cannot be written to sweep up
/// more of a third party's response than the operator named. The leading <c>$</c> is optional, so
/// the dotted form <c>SuccessJsonPath</c> uses is accepted too.
/// </remarks>
internal static class OutputPath
{
    internal const int MaxLength = 200;
    internal const int MaxSegments = 12;
    private const int MaxIndexDigits = 4;
    private const int MaxNameLength = 64;

    internal static bool TryParse(string? path, out IReadOnlyList<OutputPathSegment> segments, out string? error)
    {
        segments = [];

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "The path is empty.";
            return false;
        }

        if (path.Length > MaxLength)
        {
            error = $"The path is longer than {MaxLength} characters.";
            return false;
        }

        var found = new List<OutputPathSegment>();
        var rooted = path.StartsWith('$');
        var at = rooted ? 1 : 0;
        var first = !rooted;

        while (at < path.Length)
        {
            if (found.Count >= MaxSegments)
            {
                error = $"The path has more than {MaxSegments} steps.";
                return false;
            }

            var c = path[at];

            if (c == '[')
            {
                var close = path.IndexOf(']', at);
                var digits = close < 0 ? string.Empty : path[(at + 1)..close];

                if (digits.Length is 0 or > MaxIndexDigits || !digits.All(char.IsAsciiDigit))
                {
                    error = "A [ ] step must hold an array position such as [0].";
                    return false;
                }

                found.Add(new OutputPathSegment(null, int.Parse(digits, CultureInfo.InvariantCulture)));
                at = close + 1;
            }
            else
            {
                if (c == '.')
                {
                    at++;
                }
                else if (!first)
                {
                    error = "Steps are separated by a dot.";
                    return false;
                }

                var start = at;
                while (at < path.Length && IsNameChar(path[at])) at++;

                if (at == start || at - start > MaxNameLength)
                {
                    error = $"A step must be a property name of 1 to {MaxNameLength} letters, digits, underscores or hyphens.";
                    return false;
                }

                found.Add(new OutputPathSegment(path[start..at], null));
            }

            first = false;
        }

        if (found.Count == 0)
        {
            error = "The path names no value.";
            return false;
        }

        segments = found;
        error = null;
        return true;
    }

    private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-';
}

/// <summary>What of a response a Request step keeps, taken from its definition.</summary>
internal sealed record ResponseCapture(
    IReadOnlyDictionary<string, string> Paths, MissingOutputRule Missing, int MaxValueLength)
{
    /// <summary>Null when the definition declares no outputs.</summary>
    internal static ResponseCapture? From(RequestDefinition definition) =>
        definition.Outputs is { Count: > 0 }
            ? new ResponseCapture(
                definition.Outputs,
                definition.MissingOutput,
                definition.MaxOutputLength > 0 ? definition.MaxOutputLength : RequestDefinition.DefaultMaxOutputLength)
            : null;
}

/// <summary>Reads the values a Request step keeps out of a provider's response.</summary>
/// <remarks>
/// The response is untrusted third-party data. Only the declared paths are read. A value that is
/// missing (under the Fail rule), longer than the cap, or equal to something the request sent as a
/// credential fails the step with a message that names the output and never quotes the body.
/// </remarks>
internal static class ResponseOutputs
{
    /// <summary>The most of a response that is read to find outputs.</summary>
    internal const int MaxBodyBytes = 256 * 1024;

    private const int MaxDepth = 32;

    internal static (Dictionary<string, string>? Outputs, string? Error) Extract(
        string? body, ResponseCapture capture, IReadOnlyCollection<string> secrets)
    {
        JsonDocument doc;

        try
        {
            doc = JsonDocument.Parse(body ?? string.Empty, new JsonDocumentOptions { MaxDepth = MaxDepth });
        }
        catch (JsonException)
        {
            return (null, "The response was not JSON, so the request's outputs could not be read.");
        }

        using (doc)
        {
            var outputs = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var (name, path) in capture.Paths)
            {
                if (!OutputPath.TryParse(path, out var segments, out var pathError))
                {
                    return (null, $"Output '{name}' has a path that cannot be read: {pathError}");
                }

                var value = Read(doc.RootElement, segments);

                if (value is null)
                {
                    if (capture.Missing == MissingOutputRule.Empty)
                    {
                        outputs[name] = string.Empty;
                        continue;
                    }

                    return (null, $"Output '{name}' was not found at {path} in the response.");
                }

                if (value.Length > capture.MaxValueLength)
                {
                    return (null, $"Output '{name}' is {value.Length} characters, longer than the cap of {capture.MaxValueLength}. It was not kept.");
                }

                if (secrets.Any(secret => secret.Length > 0 && value.Contains(secret, StringComparison.Ordinal)))
                {
                    return (null, $"Output '{name}' holds a value the request sent as a credential. It was not kept.");
                }

                outputs[name] = value;
            }

            return (outputs, null);
        }
    }

    private static string? Read(JsonElement root, IReadOnlyList<OutputPathSegment> segments)
    {
        var node = root;

        foreach (var segment in segments)
        {
            if (segment.Name is { } name)
            {
                if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(name, out node)) return null;
            }
            else
            {
                var index = segment.Index!.Value;
                if (node.ValueKind != JsonValueKind.Array || index >= node.GetArrayLength()) return null;
                node = node[index];
            }
        }

        return node.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => node.GetString(),
            _ => node.GetRawText(),
        };
    }
}
