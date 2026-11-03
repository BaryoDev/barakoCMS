using System.Text;

namespace barakoCMS.Features.Public;

internal static class XmlText
{
    /// <summary>
    /// Escapes the markup characters and drops what XML 1.0 cannot carry at all. A control
    /// character in one title would otherwise make the whole document not well formed.
    /// </summary>
    public static string Escape(string value)
    {
        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                    {
                        sb.Append(c).Append(value[++i]);
                    }
                    else if (IsXmlChar(c))
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    private static bool IsXmlChar(char c) =>
        c is '\t' or '\n' or '\r'
        || (c >= 0x20 && c <= 0xD7FF)
        || (c >= 0xE000 && c <= 0xFFFD);
}
