using System.Text.Json;

namespace barakoCMS.Features.Config;

/// <summary>A configuration document read into its sections, or the reasons it could not be.</summary>
internal sealed record ParsedConfig(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> Sections,
    IReadOnlyList<string> Errors);

/// <summary>
/// The configuration document format: a JSON object holding <c>version</c> and one top-level key
/// per section, each an object from a document's key to the document.
/// </summary>
/// <remarks>
/// <para>
/// The format is a contract once someone commits a file, so it follows the three rules #345 set.
/// The document declares its version and a newer one is refused rather than half applied. An
/// unknown top-level key is refused and named rather than ignored, because a server skipping a
/// section it does not know would report success for an instance missing exactly what was asked
/// for. And an export carries what it could not represent as <c>gaps</c>, so nothing is dropped
/// without being said.
/// </para>
/// <para>
/// A section may also be an array whose items carry their key as <c>name</c>, <c>slug</c> or
/// <c>id</c>. That is how a Portability bundle holds <c>contentTypes</c> and <c>contents</c>, and
/// it is what lets a bundle be applied as a document of this format: version 1 of both formats is
/// the same number on purpose.
/// </para>
/// </remarks>
internal static class ConfigDocument
{
    public const int CurrentVersion = 1;

    public const int MaxDocumentsPerSection = 1000;

    /// <summary>The section only the seeder applies, from <c>Seed:File</c>.</summary>
    public const string TenantsSection = "tenants";

    /// <summary>Keys an export writes about itself, and a Portability bundle's own. Read and ignored.</summary>
    public static readonly IReadOnlySet<string> MetadataKeys =
        new HashSet<string>(StringComparer.Ordinal) { "version", "exportedAt", "gaps", "withheld", "contentsWithheld" };

    private static readonly string[] KeyProperties = ["name", "slug", "id"];

    public static ParsedConfig Parse(JsonElement root, IReadOnlyCollection<string> knownSections, bool allowTenants = false)
    {
        var errors = new List<string>();
        var sections = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>(StringComparer.Ordinal);

        if (root.ValueKind != JsonValueKind.Object)
        {
            return new ParsedConfig(sections, ["A configuration document is a JSON object."]);
        }

        CheckVersion(root, errors);

        foreach (var property in root.EnumerateObject())
        {
            if (MetadataKeys.Contains(property.Name) || (allowTenants && property.Name == TenantsSection))
            {
                continue;
            }

            if (property.Name == TenantsSection)
            {
                errors.Add("'tenants' is applied only when the server starts, from the file Seed:File names. "
                           + "Leave it out of a document sent to the API.");
                continue;
            }

            if (!knownSections.Contains(property.Name))
            {
                errors.Add($"'{property.Name}' is not a section this server knows, so the document is refused "
                           + "rather than applied without it. Known sections: "
                           + string.Join(", ", knownSections.OrderBy(s => s, StringComparer.Ordinal))
                           + ". A module's section is known only where the module is installed.");
                continue;
            }

            if (ReadSection(property.Name, property.Value, errors) is { } documents)
            {
                sections[property.Name] = documents;
            }
        }

        return new ParsedConfig(sections, errors);
    }

    private static void CheckVersion(JsonElement root, List<string> errors)
    {
        if (!root.TryGetProperty("version", out var version))
        {
            errors.Add($"The document declares no version. This server reads version {CurrentVersion}: add \"version\": {CurrentVersion}.");
            return;
        }

        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number < 1)
        {
            errors.Add("version must be a whole number from 1.");
            return;
        }

        if (number > CurrentVersion)
        {
            errors.Add($"The document is written for version {number} of the format, and this server reads "
                       + $"up to version {CurrentVersion}. Upgrade the server, or export from it and start from that.");
        }
    }

    private static IReadOnlyDictionary<string, JsonElement>? ReadSection(string section, JsonElement value, List<string> errors)
    {
        var documents = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var document in value.EnumerateObject())
            {
                if (!documents.TryAdd(document.Name, document.Value))
                {
                    errors.Add($"'{section}' holds the key '{document.Name}' more than once.");
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                var key = KeyOf(item) ?? $"#{index}";
                if (!documents.TryAdd(key, item))
                {
                    errors.Add($"'{section}' holds the key '{key}' more than once.");
                }

                index++;
            }
        }
        else
        {
            errors.Add($"'{section}' must be an object from key to document.");
            return null;
        }

        if (documents.Count > MaxDocumentsPerSection)
        {
            errors.Add($"'{section}' holds {documents.Count} documents, and a section holds at most {MaxDocumentsPerSection}.");
            return null;
        }

        return documents;
    }

    private static string? KeyOf(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in KeyProperties)
        {
            foreach (var property in item.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                {
                    return property.Value.GetString();
                }
            }
        }

        return null;
    }
}
