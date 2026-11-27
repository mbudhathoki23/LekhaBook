using LekhaBook.Contracts;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace LekhaBook.Api.Import;

/// <summary>
/// Rebuilds a table from the text of a PDF. A PDF has no cells, only words at positions, so this is a best
/// effort: words on one line form a row, wide gaps separate cells, and the empty strips that run down the page
/// separate columns. The screen shows the result before anything is saved. Scanned pages have no text.
/// </summary>
internal static class PdfTables
{
    private const int MaxPages = 100;

    /// <summary>A run of words close together: one cell of a line.</summary>
    private sealed record Segment(double Left, double Right, string Text);

    public static void Read(byte[] content, FileTableReader.TableCollector collector)
    {
        using var document = PdfDocument.Open(content);
        if (document.NumberOfPages > MaxPages)
        {
            throw new ImportFileException($"This PDF has more than {MaxPages} pages. Split it and try again.");
        }

        var lines = new List<List<Segment>>();
        for (var number = 1; number <= document.NumberOfPages && lines.Count <= ImportLimits.MaxRows; number++)
        {
            lines.AddRange(Lines(document.GetPage(number)));
        }

        if (lines.Count == 0)
        {
            return;
        }

        var columns = Columns(lines);
        collector.Add("PDF", lines.Select(line => Row(line, columns)));
    }

    /// <summary>The page's lines from top to bottom, each split into segments from left to right.</summary>
    private static IEnumerable<List<Segment>> Lines(Page page)
    {
        var words = page.GetWords()
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .OrderByDescending(w => w.BoundingBox.Bottom)
            .ToList();

        var line = new List<Word>();
        var baseline = 0.0;
        foreach (var word in words)
        {
            var tolerance = Math.Max(2, Height(word) * 0.5);
            if (line.Count > 0 && baseline - word.BoundingBox.Bottom > tolerance)
            {
                yield return Segments(line);
                line = [];
            }

            if (line.Count == 0)
            {
                baseline = word.BoundingBox.Bottom;
            }

            line.Add(word);
        }

        if (line.Count > 0)
        {
            yield return Segments(line);
        }
    }

    private static double Height(Word word) => word.Letters.Count == 0 ? word.BoundingBox.Height : word.Letters.Max(l => l.PointSize);

    private static List<Segment> Segments(List<Word> line)
    {
        var result = new List<Segment>();
        foreach (var word in line.OrderBy(w => w.BoundingBox.Left))
        {
            var left = word.BoundingBox.Left;
            var right = word.BoundingBox.Right;

            // A space between words is about a quarter of the text size; a column gap is much wider.
            var gap = Math.Max(4, Height(word) * 0.8);
            if (result.Count > 0 && left - result[^1].Right < gap)
            {
                var last = result[^1];
                result[^1] = new Segment(last.Left, Math.Max(last.Right, right), $"{last.Text} {word.Text}");
            }
            else
            {
                result.Add(new Segment(left, right, word.Text));
            }
        }

        return result;
    }

    /// <summary>
    /// The horizontal ranges that hold text in the table's lines. Lines with only one or two segments (titles,
    /// notes) don't count, and in long tables a rare line that crosses a gap doesn't merge two columns.
    /// </summary>
    private static List<(double Left, double Right)> Columns(List<List<Segment>> lines)
    {
        var tableLines = lines.Where(l => l.Count >= 3).ToList();
        if (tableLines.Count == 0)
        {
            tableLines = lines;
        }

        var origin = tableLines.SelectMany(l => l).Min(s => s.Left);
        var end = tableLines.SelectMany(l => l).Max(s => s.Right);
        var coverage = new int[(int)Math.Clamp(end - origin, 0, 20_000) + 2];
        foreach (var line in tableLines)
        {
            foreach (var segment in line)
            {
                var from = Math.Clamp((int)(segment.Left - origin), 0, coverage.Length - 1);
                var to = Math.Clamp((int)(segment.Right - origin), from, coverage.Length - 1);
                for (var x = from; x <= to; x++)
                {
                    coverage[x]++;
                }
            }
        }

        var threshold = tableLines.Count / 20;
        var columns = new List<(double Left, double Right)>();
        int? start = null;
        for (var x = 0; x < coverage.Length; x++)
        {
            if (coverage[x] > threshold)
            {
                start ??= x;
            }
            else if (start is { } s)
            {
                columns.Add((origin + s, origin + x));
                start = null;
            }
        }

        if (start is { } open)
        {
            columns.Add((origin + open, end));
        }

        return columns.Count > 0 ? columns : [(origin, end)];
    }

    /// <summary>Puts each segment in the column it overlaps most, or else the nearest one (a left-aligned heading over right-aligned amounts).</summary>
    private static IReadOnlyList<string> Row(List<Segment> line, List<(double Left, double Right)> columns)
    {
        var cells = new string[columns.Count];
        Array.Fill(cells, string.Empty);
        foreach (var segment in line)
        {
            var best = 0;
            var bestScore = double.MinValue;
            for (var i = 0; i < columns.Count; i++)
            {
                // Positive: overlapping width. Negative: distance to the column.
                var score = Math.Min(segment.Right, columns[i].Right) - Math.Max(segment.Left, columns[i].Left);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }

            cells[best] = cells[best].Length == 0 ? segment.Text : $"{cells[best]} {segment.Text}";
        }

        return cells;
    }
}
