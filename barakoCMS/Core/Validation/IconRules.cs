using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using barakoCMS.Models;

namespace barakoCMS.Core.Validation;

/// <summary>
/// What a content type's icon may be, and the sanitiser that cleans a custom SVG before it is stored.
/// </summary>
/// <remarks>
/// A sprite name is checked, and a custom SVG is rebuilt rather than filtered: the document is parsed
/// with DTD processing prohibited and no resolver (so an entity bomb or an external entity is a
/// parse error, not work), and a new tree is built holding only allowlisted elements and attributes.
/// Whatever is not on the list, including scripts, styles, <c>foreignObject</c>, links, <c>on*</c>
/// handlers and every <c>fill</c> and <c>stroke</c>, is dropped. Only an unusable document is refused.
/// </remarks>
internal static class IconRules
{
    public const int MaxNameLength = 64;

    /// <summary>The longest SVG accepted, in characters, measured before sanitising.</summary>
    public const int MaxSvgLength = 16 * 1024;

    private const int MaxDepth = 16;

    private static readonly XNamespace SvgNs = "http://www.w3.org/2000/svg";

    private static readonly HashSet<string> Shapes =
        ["path", "circle", "ellipse", "rect", "line", "polyline", "polygon"];

    private static readonly Dictionary<string, HashSet<string>> Attributes = new()
    {
        ["g"] = ["transform", "opacity"],
        ["path"] = ["d", "transform", "opacity", "fill-rule", "clip-rule"],
        ["circle"] = ["cx", "cy", "r", "transform", "opacity"],
        ["ellipse"] = ["cx", "cy", "rx", "ry", "transform", "opacity"],
        ["rect"] = ["x", "y", "width", "height", "rx", "ry", "transform", "opacity"],
        ["line"] = ["x1", "y1", "x2", "y2", "transform", "opacity"],
        ["polyline"] = ["points", "transform", "opacity"],
        ["polygon"] = ["points", "transform", "opacity", "fill-rule"],
    };

    private static readonly Regex NamePattern = new(@"\A[A-Za-z0-9-]+\z", RegexOptions.Compiled);
    private static readonly Regex ViewBoxPattern = new(
        @"\A\s*-?\d+(\.\d+)?[\s,]+-?\d+(\.\d+)?[\s,]+\d+(\.\d+)?[\s,]+\d+(\.\d+)?\s*\z", RegexOptions.Compiled);
    private static readonly Regex ValuePattern = new(@"\A[A-Za-z0-9\s.,+\-()%]*\z", RegexOptions.Compiled);
    private static readonly Regex LongToken = new(@"[0-9.]{13,}", RegexOptions.Compiled);

    public readonly record struct Result(bool Valid, ContentTypeIcon? Icon, List<string> Errors);

    /// <summary>Checks an icon and returns it as it is to be stored, SVG sanitised. Null is no icon.</summary>
    public static Result Check(ContentTypeIcon? icon)
    {
        var errors = new List<string>();
        if (icon is null) return new Result(true, null, errors);

        var hasName = !string.IsNullOrWhiteSpace(icon.Name);
        var hasSvg = !string.IsNullOrWhiteSpace(icon.Svg);

        if (hasName == hasSvg)
        {
            errors.Add("icon needs exactly one of name or svg. Send icon as null to clear it.");
            return new Result(false, null, errors);
        }

        if (hasName)
        {
            if (icon.Name!.Length > MaxNameLength || !NamePattern.IsMatch(icon.Name))
            {
                errors.Add($"icon.name must be 1 to {MaxNameLength} letters, digits or hyphens.");
                return new Result(false, null, errors);
            }

            return new Result(true, new ContentTypeIcon { Name = icon.Name }, errors);
        }

        if (icon.Svg!.Length > MaxSvgLength)
        {
            errors.Add($"icon.svg is {icon.Svg.Length} characters. The most allowed is {MaxSvgLength}.");
            return new Result(false, null, errors);
        }

        var clean = Sanitise(icon.Svg, out var problem);
        if (clean is null)
        {
            errors.Add($"icon.svg {problem}");
            return new Result(false, null, errors);
        }

        return new Result(true, new ContentTypeIcon { Svg = clean }, errors);
    }

    /// <summary>The cleaned markup, or null with the reason the document cannot be used.</summary>
    public static string? Sanitise(string svg, out string problem)
    {
        problem = string.Empty;
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
                MaxCharactersInDocument = MaxSvgLength * 2,
            };
            using var reader = XmlReader.Create(new StringReader(svg), settings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            problem = "is not well-formed XML, or declares a DOCTYPE or entities, which are not accepted.";
            return null;
        }

        var root = doc.Root;
        if (root is null || root.Name.LocalName != "svg" || (root.Name.Namespace != SvgNs && root.Name.Namespace != XNamespace.None))
        {
            problem = "must have an svg element as its root.";
            return null;
        }

        var viewBox = root.Attribute("viewBox")?.Value;
        if (string.IsNullOrWhiteSpace(viewBox))
        {
            problem = "has no viewBox.";
            return null;
        }

        if (!ViewBoxPattern.IsMatch(viewBox))
        {
            problem = "has a viewBox that is not four numbers (min-x min-y width height).";
            return null;
        }

        var clean = new XElement(SvgNs + "svg", new XAttribute("viewBox", viewBox.Trim()));
        var shapes = Copy(root, clean, 1);
        if (shapes == 0)
        {
            problem = "has no shape. Use path, circle, ellipse, rect, line, polyline or polygon.";
            return null;
        }

        return clean.ToString(SaveOptions.DisableFormatting);
    }

    private static int Copy(XElement from, XElement into, int depth)
    {
        if (depth > MaxDepth) return 0;

        var shapes = 0;
        foreach (var child in from.Elements())
        {
            if (child.Name.Namespace != SvgNs && child.Name.Namespace != XNamespace.None) continue;

            var name = child.Name.LocalName;
            var isShape = Shapes.Contains(name);
            if (!isShape && name != "g") continue;

            var copy = new XElement(SvgNs + name);
            foreach (var attribute in child.Attributes())
            {
                if (attribute.Name.Namespace != XNamespace.None) continue;
                if (!Attributes[name].Contains(attribute.Name.LocalName)) continue;
                if (!IsSafe(attribute.Value)) continue;
                copy.SetAttributeValue(attribute.Name.LocalName, attribute.Value);
            }

            if (isShape)
            {
                copy.SetAttributeValue("fill", "currentColor");
                shapes++;
            }
            else
            {
                shapes += Copy(child, copy, depth + 1);
            }

            into.Add(copy);
        }

        return shapes;
    }

    private static bool IsSafe(string value) =>
        ValuePattern.IsMatch(value) && !LongToken.IsMatch(value) && !value.Contains("url(", StringComparison.OrdinalIgnoreCase);
}
