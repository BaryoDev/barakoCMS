using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using barakoCMS.Infrastructure.Services;
using barakoCMS.Models;

namespace barakoCMS.Features.ContentType.Blueprints;

/// <summary>
/// The blueprints this instance offers: the four built in, plus every <c>*.json</c> file in
/// <c>Blueprints:Path</c> when that is set.
/// </summary>
/// <remarks>
/// Custom files are read and validated on every call rather than cached at startup, so an operator
/// can drop a file in and list it without a restart, and a broken file is reported by the list
/// rather than discovered by the apply. Validation is the same <see cref="IContentTypeValidatorService"/>
/// the create endpoint runs, so a blueprint cannot describe a type the API would refuse.
/// </remarks>
internal sealed class BlueprintCatalog
{
    public const string PathKey = "Blueprints:Path";

    /// <summary>How many custom files the list reads. Past this the directory is reported, not read.</summary>
    public const int MaxCustomFiles = 100;

    /// <summary>How large a custom file may be. A larger file is reported invalid, never parsed.</summary>
    public const int MaxCustomFileBytes = 256 * 1024;

    private const string ResourcePrefix = "Blueprints/";

    private static readonly Regex NameShape = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);

    private static readonly Lazy<IReadOnlyList<(string Resource, string Json)>> BuiltInFiles = new(ReadBuiltInFiles);

    /// <summary>
    /// Case-insensitive names and string enums, like the HTTP serializer, and unknown members refused
    /// so that a misspelt property is an error in the list rather than a field quietly left Public.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new JsonStringEnumConverter(),
            new barakoCMS.Infrastructure.Serialization.ObjectJsonConverter(),
        },
    };

    private readonly IConfiguration _configuration;
    private readonly IContentTypeValidatorService _validator;
    private readonly ILogger<BlueprintCatalog> _logger;

    public BlueprintCatalog(
        IConfiguration configuration,
        IContentTypeValidatorService validator,
        ILogger<BlueprintCatalog> logger)
    {
        _configuration = configuration;
        _validator = validator;
        _logger = logger;
    }

    public string? CustomPath => _configuration[PathKey] is { Length: > 0 } path ? path : null;

    /// <summary>Every blueprint, built-ins first, each group sorted by name.</summary>
    /// <param name="problems">Directory-level problems: a path that does not exist, or too many files.</param>
    public IReadOnlyList<BlueprintEntry> All(out IReadOnlyList<string> problems)
    {
        var entries = new List<BlueprintEntry>();

        foreach (var (resource, json) in BuiltInFiles.Value)
        {
            entries.Add(Load(json, resource[ResourcePrefix.Length..], builtIn: true, taken: entries));
        }

        var found = new List<string>();
        var path = CustomPath;
        if (path is not null)
        {
            if (!Directory.Exists(path))
            {
                found.Add($"{PathKey} is set but the directory does not exist.");
                _logger.LogWarning("Blueprints directory is configured but missing");
            }
            else
            {
                var files = Directory.EnumerateFiles(path, "*.json")
                    .OrderBy(f => f, StringComparer.Ordinal)
                    .ToList();

                if (files.Count > MaxCustomFiles)
                {
                    found.Add($"{PathKey} holds {files.Count} files and only the first {MaxCustomFiles} are read.");
                }

                foreach (var file in files.Take(MaxCustomFiles))
                {
                    if (new FileInfo(file).Length > MaxCustomFileBytes)
                    {
                        entries.Add(TooLarge(file));
                        continue;
                    }

                    string json;
                    try
                    {
                        json = File.ReadAllText(file);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // The OS message carries the absolute server path (IOException especially),
                        // which is not this caller's business even with manage_content_types. The
                        // full exception still goes to the log, keyed on the base name there too.
                        _logger.LogWarning(ex, "Custom blueprint {File} could not be read", Path.GetFileName(file));
                        entries.Add(Unreadable(file));
                        continue;
                    }

                    entries.Add(Load(json, Path.GetFileName(file), builtIn: false, taken: entries));
                }
            }
        }

        problems = found;
        return entries;
    }

    /// <summary>The blueprint with this name, or null. Names are compared normalized.</summary>
    public BlueprintEntry? Find(string name)
    {
        var wanted = Normalize(name);
        return All(out _).FirstOrDefault(e => e.Name == wanted);
    }

    /// <summary>
    /// Fresh definitions for applying, each with its own id and a normalized name, so the parsed
    /// blueprint is never the object handed to the session.
    /// </summary>
    public static List<ContentTypeDefinition> Materialize(Blueprint blueprint)
    {
        var copy = JsonSerializer.Deserialize<List<ContentTypeDefinition>>(
            JsonSerializer.Serialize(blueprint.ContentTypes, Json), Json) ?? new();

        var now = DateTimeOffset.UtcNow;
        foreach (var type in copy)
        {
            type.Id = Guid.NewGuid();
            type.Name = barakoCMS.Core.ContentTypeName.Normalize(type.Name);
            type.CreatedAt = now;
            type.UpdatedAt = now;
            foreach (var field in type.Fields.Where(f => !string.IsNullOrWhiteSpace(f.ReferenceType)))
            {
                field.ReferenceType = barakoCMS.Core.ContentTypeName.Normalize(field.ReferenceType);
            }
        }

        return copy;
    }

    public static string Normalize(string? name) => (name ?? string.Empty).Trim().ToLowerInvariant();

    private BlueprintEntry Load(string json, string source, bool builtIn, List<BlueprintEntry> taken)
    {
        Blueprint? blueprint;
        try
        {
            blueprint = JsonSerializer.Deserialize<Blueprint>(json, Json);
        }
        catch (JsonException ex)
        {
            return Unreadable(source, ex.Message);
        }

        if (blueprint is null)
        {
            return Unreadable(source, "the file is empty.");
        }

        var name = Normalize(blueprint.Name);
        var errors = new List<string>();

        if (name.Length == 0)
        {
            errors.Add("A blueprint needs a name.");
            name = Normalize(Path.GetFileNameWithoutExtension(source));
        }
        else if (!NameShape.IsMatch(name))
        {
            errors.Add($"The name '{blueprint.Name}' must be lower-case letters, digits and hyphens.");
        }

        var clash = taken.FirstOrDefault(e => e.Name == name);
        if (clash is not null)
        {
            errors.Add(clash.BuiltIn
                ? $"'{name}' is a built-in blueprint, so this file is not listed under that name."
                : $"'{name}' is already declared by {clash.Source}.");
        }

        errors.AddRange(ValidateTypes(blueprint));

        return new BlueprintEntry
        {
            Name = name,
            Description = blueprint.Description ?? string.Empty,
            BuiltIn = builtIn,
            Source = builtIn ? null : source,
            ContentTypes = blueprint.ContentTypes
                .Select(t => barakoCMS.Core.ContentTypeName.Normalize(t.Name))
                .Where(n => n.Length > 0)
                .ToList(),
            Errors = errors,
            Definition = blueprint,
        };
    }

    private List<string> ValidateTypes(Blueprint blueprint)
    {
        var errors = new List<string>();

        if (blueprint.ContentTypes.Count == 0)
        {
            errors.Add("A blueprint needs at least one content type.");
            return errors;
        }

        var names = blueprint.ContentTypes
            .Select(t => barakoCMS.Core.ContentTypeName.Normalize(t.Name))
            .ToList();

        foreach (var duplicate in names.Where(n => n.Length > 0).GroupBy(n => n).Where(g => g.Count() > 1))
        {
            errors.Add($"Type '{duplicate.Key}' is declared more than once.");
        }

        var declared = names.ToHashSet(StringComparer.Ordinal);

        foreach (var type in blueprint.ContentTypes)
        {
            var label = type.Name is { Length: > 0 } ? type.Name : "(unnamed)";

            var (valid, typeErrors) = _validator.Validate(type.Name, type.DisplayName, type.Fields);
            if (!valid)
            {
                errors.AddRange(typeErrors.Select(e => $"Type '{label}': {e}"));
            }

            var (lifecycleValid, lifecycleErrors) = _validator.ValidateLifecycle(type.Lifecycle);
            if (!lifecycleValid)
            {
                errors.AddRange(lifecycleErrors.Select(e => $"Type '{label}': {e}"));
            }

            // A reference has to point at a type in the same blueprint. Applying is meant to produce
            // a schema that works on an empty tenant, and a target that only exists on some tenants
            // is a picker that sometimes has nothing to pick from.
            foreach (var field in type.Fields)
            {
                if (string.IsNullOrWhiteSpace(field.ReferenceType)) continue;

                var target = barakoCMS.Core.ContentTypeName.Normalize(field.ReferenceType);
                if (!declared.Contains(target))
                {
                    errors.Add(
                        $"Type '{label}': field '{field.Name}' references '{field.ReferenceType}', "
                        + "which this blueprint does not declare.");
                }
            }
        }

        return errors;
    }

    private static BlueprintEntry Unreadable(string source, string reason) => new()
    {
        Name = Normalize(Path.GetFileNameWithoutExtension(source)),
        BuiltIn = false,
        Source = Path.GetFileName(source),
        Errors = [$"{Path.GetFileName(source)} could not be read: {reason}"],
    };

    /// <summary>An OS-level read failure (permissions, a locked file). No OS message here: it can
    /// carry the absolute server path, and the caller only needs the file's name.</summary>
    private static BlueprintEntry Unreadable(string source) => new()
    {
        Name = Normalize(Path.GetFileNameWithoutExtension(source)),
        BuiltIn = false,
        Source = Path.GetFileName(source),
        Errors = [$"{Path.GetFileName(source)} cannot be read."],
    };

    private static BlueprintEntry TooLarge(string source) => new()
    {
        Name = Normalize(Path.GetFileNameWithoutExtension(source)),
        BuiltIn = false,
        Source = Path.GetFileName(source),
        Errors = [$"{Path.GetFileName(source)} is larger than {MaxCustomFileBytes / 1024} KB and was not read."],
    };

    private static IReadOnlyList<(string Resource, string Json)> ReadBuiltInFiles()
    {
        var assembly = typeof(BlueprintCatalog).Assembly;
        var files = new List<(string, string)>();

        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Embedded blueprint {resource} is listed but not readable.");
            using var reader = new StreamReader(stream);
            files.Add((resource, reader.ReadToEnd()));
        }

        return files;
    }
}
