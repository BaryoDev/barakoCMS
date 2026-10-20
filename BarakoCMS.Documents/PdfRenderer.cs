using barakoCMS.Models;
using SkiaSharp;

namespace BarakoCMS.Documents;

/// <summary>Draws a parsed template to PDF bytes with SkiaSharp. Text and rules only.</summary>
public static class PdfRenderer
{
    public const int MaxPages = 50;

    private const float Margin = 50;

    public static byte[] Render(ParsedTemplate template, Content content)
    {
        var (width, height) = template.Page == "Letter" ? (612f, 792f) : (595f, 842f);

        using var output = new MemoryStream();
        // The entry's own timestamps, so the same entry renders to the same bytes.
        var metadata = new SKDocumentPdfMetadata { Creation = content.UpdatedAt, Modified = content.UpdatedAt };

        using (var document = SKDocument.CreatePdf(output, metadata))
        {
            var pages = new PageWriter(document, width, height);

            foreach (var block in template.Blocks)
            {
                pages.Draw(block, content);
            }

            pages.Finish();
        }

        return output.ToArray();
    }

    private sealed class PageWriter(SKDocument document, float width, float height)
    {
        private readonly SKTypeface _regular = SKTypeface.Default;
        private readonly SKTypeface _bold = SKTypeface.FromFamilyName(null, SKFontStyle.Bold) ?? SKTypeface.Default;
        private readonly SKPaint _ink = new() { Color = SKColors.Black, IsAntialias = true, StrokeWidth = 0.75f };
        private SKCanvas? _canvas;
        private float _y;
        private int _count;

        public void Draw(Block block, Content content)
        {
            switch (block.Kind)
            {
                case BlockKind.Title: Lines(DocumentValues.Fill(block.Cells[0], content), _bold, 22, Margin, Usable); break;
                case BlockKind.Heading: Lines(DocumentValues.Fill(block.Cells[0], content), _bold, 14, Margin, Usable); break;
                case BlockKind.Paragraph: Lines(DocumentValues.Fill(block.Cells[0], content), _regular, 11, Margin, Usable); break;
                case BlockKind.Gap: Advance(8); break;
                case BlockKind.Rule: Rule(); break;
                case BlockKind.Row: Row(block.Cells.Select(c => DocumentValues.Fill(c, content)).ToArray()); break;
            }
        }

        public void Finish()
        {
            if (_canvas is not null) document.EndPage();
            _canvas = null;
            _ink.Dispose();
        }

        private float Usable => width - 2 * Margin;

        private void Rule()
        {
            Advance(6);
            Ensure(1);
            _canvas!.DrawLine(Margin, _y, width - Margin, _y, _ink);
            Advance(6);
        }

        private void Row(string[] cells)
        {
            var column = Usable / cells.Length;
            var wrapped = cells.Select(c => Wrap(c, _regular, 11, column - 8)).ToArray();
            var lineHeight = 11 * 1.35f;
            var rows = Math.Max(1, wrapped.Max(w => w.Count));

            // One line at a time, so a cell longer than a page continues on the next page, and the
            // page limit still stops a huge value.
            for (var l = 0; l < rows; l++)
            {
                Ensure(lineHeight);
                for (var i = 0; i < wrapped.Length; i++)
                {
                    if (l < wrapped[i].Count)
                    {
                        DrawText(wrapped[i][l], _regular, 11, Margin + i * column, _y + lineHeight - 3);
                    }
                }

                _y += lineHeight;
            }
        }

        private void Lines(string text, SKTypeface face, float size, float x, float usable)
        {
            var lineHeight = size * 1.35f;
            foreach (var line in Wrap(text, face, size, usable))
            {
                Ensure(lineHeight);
                DrawText(line, face, size, x, _y + lineHeight - 3);
                _y += lineHeight;
            }
        }

        private void DrawText(string text, SKTypeface face, float size, float x, float baseline)
        {
            using var font = new SKFont(face, size);
            _canvas!.DrawText(text, x, baseline, SKTextAlign.Left, font, _ink);
        }

        private static List<string> Wrap(string text, SKTypeface face, float size, float usable)
        {
            using var font = new SKFont(face, size);
            var lines = new List<string>();

            foreach (var paragraph in text.Replace("\r", string.Empty).Split('\n'))
            {
                var line = string.Empty;
                foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var next = line.Length == 0 ? word : line + " " + word;
                    if (line.Length > 0 && font.MeasureText(next) > usable)
                    {
                        lines.Add(line);
                        line = word;
                    }
                    else
                    {
                        line = next;
                    }
                }

                lines.Add(line);
            }

            return lines;
        }

        private void Advance(float amount)
        {
            if (_canvas is null) return;
            _y += amount;
        }

        private void Ensure(float needed)
        {
            if (_canvas is not null && _y + needed <= height - Margin) return;

            if (_canvas is not null) document.EndPage();
            if (++_count > MaxPages)
            {
                throw new DocumentTooLongException(MaxPages);
            }

            _canvas = document.BeginPage(width, height);
            _y = Margin;
        }
    }
}

public sealed class DocumentTooLongException(int pages)
    : Exception($"The document is longer than {pages} pages.");
