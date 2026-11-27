using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using LekhaBook.Contracts;

namespace LekhaBook.Api.Import;

/// <summary>Every data table of an HTML page. The page is only parsed: no scripts run and nothing is fetched.</summary>
internal static class HtmlTables
{
    public static bool LooksLikeHtml(string text)
    {
        var start = text.AsSpan().TrimStart();
        return start.StartsWith("<") && text.Contains("<table", StringComparison.OrdinalIgnoreCase)
            || start.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
            || start.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    public static void Read(string html, FileTableReader.TableCollector collector)
    {
        var document = new HtmlParser().ParseDocument(html);
        var number = 0;
        foreach (var table in document.QuerySelectorAll("table").OfType<IHtmlTableElement>())
        {
            // A table that only lays out other tables holds no data of its own.
            if (table.QuerySelector("table") is not null)
            {
                continue;
            }

            if (collector.IsFull)
            {
                break;
            }

            number++;
            var caption = table.Caption?.TextContent.Trim();
            var rows = table.Rows.Select(Cells).ToList();

            // One-column or one-row tables are usually page layout.
            if (rows.Count(r => r.Count(c => c.Length > 0) > 1) > 0)
            {
                collector.Add(string.IsNullOrEmpty(caption) ? $"Table {number}" : caption, rows);
            }
        }
    }

    private static List<string> Cells(IHtmlTableRowElement row)
    {
        var cells = new List<string>();
        foreach (var cell in row.Cells.OfType<IHtmlTableCellElement>())
        {
            cells.Add(Text(cell));

            // Keep later cells under their headings.
            for (var i = 1; i < cell.ColumnSpan && cells.Count <= ImportLimits.MaxColumns; i++)
            {
                cells.Add(string.Empty);
            }

            if (cells.Count > ImportLimits.MaxColumns)
            {
                break;
            }
        }

        return cells;
    }

    /// <summary>
    /// The cell's visible text on one line. Walked with a stack, not recursion: a hostile file can nest elements
    /// deeply enough to overflow the call stack and stop the server.
    /// </summary>
    private static string Text(IElement cell)
    {
        var text = new StringBuilder();
        var pending = new Stack<INode>();
        pending.Push(cell);
        while (pending.TryPop(out var node))
        {
            if (node is IText value)
            {
                text.Append(value.Data);
                continue;
            }

            if (node is IElement element)
            {
                if (element.LocalName is "script" or "style")
                {
                    continue;
                }

                if (element.LocalName is "br" or "div" or "p" or "li")
                {
                    text.Append(' ');
                }
            }

            // Pushed last to first, so they come off in reading order.
            for (var i = node.ChildNodes.Length - 1; i >= 0; i--)
            {
                pending.Push(node.ChildNodes[i]);
            }
        }

        return string.Join(' ', text.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
