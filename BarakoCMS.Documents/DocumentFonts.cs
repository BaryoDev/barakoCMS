using System.Reflection;
using SkiaSharp;

namespace BarakoCMS.Documents;

/// <summary>
/// The faces a document is drawn with, embedded in this assembly. The Suite runs on an image with no
/// system fonts and SkiaSharp's no-dependencies Linux build has no font manager, so the default
/// typeface there has no glyphs and a PDF would come out with no visible text. Liberation Sans is
/// SIL OFL 1.1, the licence text is embedded beside it.
/// </summary>
internal static class DocumentFonts
{
    private static readonly Lazy<SKTypeface> RegularFace = new(() => Load("LiberationSans-Regular.ttf"));
    private static readonly Lazy<SKTypeface> BoldFace = new(() => Load("LiberationSans-Bold.ttf"));

    /// <summary>Shared for the life of the process. Never dispose it.</summary>
    public static SKTypeface Regular => RegularFace.Value;

    /// <summary>Shared for the life of the process. Never dispose it.</summary>
    public static SKTypeface Bold => BoldFace.Value;

    private static SKTypeface Load(string file)
    {
        using var stream = typeof(DocumentFonts).Assembly.GetManifestResourceStream("barako-fonts/" + file)
            ?? throw new InvalidOperationException($"The embedded font {file} is missing from the Documents assembly.");
        return SKTypeface.FromStream(stream)
            ?? throw new InvalidOperationException($"The embedded font {file} could not be read.");
    }
}
