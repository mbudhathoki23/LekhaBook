using System.Text;

namespace LekhaBook.Api.Import;

/// <summary>Comma, semicolon, tab or pipe separated text, with quoted cells (RFC 4180).</summary>
internal static class CsvTables
{
    private static readonly char[] Delimiters = [',', ';', '\t', '|'];

    public static void Read(string text, string name, FileTableReader.TableCollector collector)
    {
        // Excel writes "sep=;" as a first line to name the separator.
        char? declared = null;
        if (text.StartsWith("sep=", StringComparison.OrdinalIgnoreCase) && text.Length > 4)
        {
            declared = text[4];
            var end = text.IndexOf('\n');
            text = end < 0 ? string.Empty : text[(end + 1)..];
        }

        collector.Add(name, Parse(text, declared ?? Sniff(text)));
    }

    /// <summary>The delimiter that appears most, outside quotes, in the first lines.</summary>
    private static char Sniff(string text)
    {
        var counts = new int[Delimiters.Length];
        var quoted = false;
        var lines = 0;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (!quoted)
            {
                if (c == '\n' && ++lines == 20)
                {
                    break;
                }

                var index = Array.IndexOf(Delimiters, c);
                if (index >= 0)
                {
                    counts[index]++;
                }
            }
        }

        return Delimiters[Array.IndexOf(counts, counts.Max())];
    }

    private static IEnumerable<IReadOnlyList<string>> Parse(string text, char delimiter)
    {
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"')
                {
                    cell.Append(c);
                }
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }
            }
            else if (c == '"' && cell.Length == 0)
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                row.Add(Take(cell));
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(Take(cell));
                yield return row;
                row = [];
            }
            else
            {
                cell.Append(c);
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(Take(cell));
            yield return row;
        }
    }

    private static string Take(StringBuilder cell)
    {
        var value = cell.ToString();
        cell.Clear();
        return value;
    }
}
