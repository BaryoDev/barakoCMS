using System.Text.RegularExpressions;
using barakoCMS.Models;

namespace BarakoCMS.Documents;

internal static class DocumentValues
{
    private static readonly Regex Hole = new(@"\{\{\s*([A-Za-z0-9_.\[\]]+)\s*\}\}", RegexOptions.Compiled);

    /// <summary>Fills each hole from the entry. A hole with no value becomes empty text.</summary>
    public static string Fill(string text, Content content) =>
        Hole.Replace(text, m => Value(m.Groups[1].Value, content) ?? string.Empty);

    private static string? Value(string name, Content content)
    {
        switch (name.ToLowerInvariant())
        {
            case "id": return content.Id.ToString();
            case "contenttype": return content.ContentType;
            case "status": return content.Status.ToString();
            case "createdat": return content.CreatedAt.ToString("yyyy-MM-dd");
            case "updatedat": return content.UpdatedAt.ToString("yyyy-MM-dd");
        }

        var bare = name.StartsWith("data.", StringComparison.OrdinalIgnoreCase) ? name[5..] : name;
        foreach (var (key, value) in content.Data)
        {
            if (string.Equals(key, bare, StringComparison.OrdinalIgnoreCase)) return value?.ToString();
        }

        return null;
    }
}
