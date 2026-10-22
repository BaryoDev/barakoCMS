namespace BarakoCMS.Documents;

public enum BlockKind { Title, Heading, Paragraph, Rule, Row, Gap }

/// <param name="Cells">The text of the block. A row has one entry per column, anything else has one.</param>
public sealed record Block(BlockKind Kind, IReadOnlyList<string> Cells);

public sealed record ParsedTemplate(string Page, IReadOnlyList<Block> Blocks, IReadOnlyList<string> Errors)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// The whole template language. One line is one block:
/// <code>
/// @page A4            optional, first, A4 (default) or Letter
/// # Title
/// ## Heading
/// ---                 a horizontal rule
/// | Item | Qty | Price |   a row, columns of equal width
/// (blank line)        a gap
/// anything else       a paragraph, wrapped to the page
/// </code>
/// Text may hold <c>{{field}}</c> holes. There is no image, link, font or include syntax, and the
/// renderer only draws text and rules, so a template has nothing to fetch with. A line starting with
/// <c>@</c> that is not <c>@page</c> is refused rather than drawn as text.
/// </summary>
public static class DocumentTemplateParser
{
    public const int MaxLines = 500;
    public const int MaxLength = 20_000;
    public const int MaxColumns = 8;

    public static ParsedTemplate Parse(string? source)
    {
        var errors = new List<string>();
        var blocks = new List<Block>();
        var page = "A4";

        source ??= string.Empty;
        if (source.Length > MaxLength)
        {
            return new ParsedTemplate(page, blocks, [$"The template is longer than {MaxLength} characters."]);
        }

        var lines = source.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > MaxLines)
        {
            return new ParsedTemplate(page, blocks, [$"The template has more than {MaxLines} lines."]);
        }

        var seenContent = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var number = i + 1;
            var line = lines[i].TrimEnd();

            if (line.StartsWith('@'))
            {
                var parts = line[1..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && parts[0] == "page" && !seenContent
                    && (parts[1] == "A4" || parts[1] == "Letter"))
                {
                    page = parts[1];
                }
                else
                {
                    errors.Add($"Line {number}: '{line}' is not allowed. The only directive is '@page A4' or '@page Letter', before any other line.");
                }

                continue;
            }

            if (line.Length == 0)
            {
                if (seenContent) blocks.Add(new Block(BlockKind.Gap, [string.Empty]));
                continue;
            }

            seenContent = true;

            if (line == "---") blocks.Add(new Block(BlockKind.Rule, [string.Empty]));
            else if (line.StartsWith("## ")) blocks.Add(new Block(BlockKind.Heading, [line[3..].Trim()]));
            else if (line.StartsWith("# ")) blocks.Add(new Block(BlockKind.Title, [line[2..].Trim()]));
            else if (line.StartsWith('|'))
            {
                var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                if (cells.Length > MaxColumns)
                {
                    errors.Add($"Line {number}: a row has at most {MaxColumns} columns.");
                    continue;
                }

                blocks.Add(new Block(BlockKind.Row, cells));
            }
            else blocks.Add(new Block(BlockKind.Paragraph, [line]));
        }

        return new ParsedTemplate(page, blocks, errors);
    }
}
