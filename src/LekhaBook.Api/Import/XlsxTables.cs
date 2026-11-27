using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using LekhaBook.Contracts;

namespace LekhaBook.Api.Import;

/// <summary>
/// The visible sheets of an .xlsx workbook, read straight from its zipped XML. Cells formatted as dates become
/// "yyyy-MM-dd" and numbers become plain decimals; formulas give their last saved value.
/// </summary>
internal static class XlsxTables
{
    /// <summary>Most XML that will be unzipped from one workbook, so a small "zip bomb" can't fill memory.</summary>
    private const long MaxUnzippedBytes = 40L * 1024 * 1024;

    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static void Read(byte[] content, FileTableReader.TableCollector collector)
    {
        using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
        var budget = MaxUnzippedBytes;

        var workbook = Load(zip, "xl/workbook.xml", ref budget)
            ?? throw new ImportFileException("This isn't an Excel workbook. Use CSV, Excel (.xlsx), HTML or PDF.");
        var targets = (Load(zip, "xl/_rels/workbook.xml.rels", ref budget)?.Root?.Elements(PackageRel + "Relationship") ?? [])
            .ToDictionary(r => (string)r.Attribute("Id")!, r => (string)r.Attribute("Target")!);

        var strings = Load(zip, "xl/sharedStrings.xml", ref budget)?.Root?.Elements(Main + "si").Select(Text).ToArray() ?? [];
        var dateStyles = DateStyles(Load(zip, "xl/styles.xml", ref budget));
        var date1904 = (string?)workbook.Root?.Element(Main + "workbookPr")?.Attribute("date1904") is "1" or "true";

        foreach (var sheet in workbook.Root?.Element(Main + "sheets")?.Elements(Main + "sheet") ?? [])
        {
            if (collector.IsFull)
            {
                break;
            }

            if ((string?)sheet.Attribute("state") is "hidden" or "veryHidden"
                || (string?)sheet.Attribute(Rel + "id") is not { } id || !targets.TryGetValue(id, out var target))
            {
                continue;
            }

            var path = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            if (Load(zip, path, ref budget) is { } xml)
            {
                collector.Add((string?)sheet.Attribute("name") ?? "Sheet", Rows(xml, strings, dateStyles, date1904));
            }
        }
    }

    private static IEnumerable<IReadOnlyList<string>> Rows(XDocument sheet, string[] strings, HashSet<int> dateStyles, bool date1904)
    {
        foreach (var row in sheet.Root?.Element(Main + "sheetData")?.Elements(Main + "row") ?? [])
        {
            var cells = new List<string>();
            foreach (var cell in row.Elements(Main + "c"))
            {
                // Empty cells are left out of the file, so the cell's reference ("C5") gives its column.
                var column = ColumnOf((string?)cell.Attribute("r")) ?? cells.Count;
                if (column > ImportLimits.MaxColumns)
                {
                    column = ImportLimits.MaxColumns; // far-right cells are dropped by the collector
                }

                while (cells.Count < column)
                {
                    cells.Add(string.Empty);
                }

                cells.Add(Value(cell, strings, dateStyles, date1904));
            }

            yield return cells;
        }
    }

    private static string Value(XElement cell, string[] strings, HashSet<int> dateStyles, bool date1904)
    {
        var type = (string?)cell.Attribute("t");
        if (type == "inlineStr")
        {
            return cell.Element(Main + "is") is { } inline ? Text(inline) : string.Empty;
        }

        var raw = (string?)cell.Element(Main + "v");
        if (raw is null)
        {
            return string.Empty;
        }

        switch (type)
        {
            case "s":
                return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < strings.Length
                    ? strings[index]
                    : string.Empty;
            case "str" or "d":
                return raw;
            case "b":
                return raw == "1" ? "TRUE" : "FALSE";
            case "e":
                return string.Empty;
        }

        if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return raw;
        }

        var style = (int?)cell.Attribute("s") ?? 0;
        if (dateStyles.Contains(style) && number >= 1 && number < 2_958_466)
        {
            // Day 0 is 1899-12-30 (or 1904-01-01 in old Mac workbooks).
            var epoch = date1904 ? new DateOnly(1904, 1, 1) : new DateOnly(1899, 12, 30);
            var day = (int)decimal.Truncate(number);
            return day <= DateOnly.MaxValue.DayNumber - epoch.DayNumber
                ? epoch.AddDays(day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : raw;
        }

        // Excel stores 1234.56 as 1234.5600000000001; nine places keeps real decimals and drops that noise.
        return decimal.Round(number, 9).ToString("0.#########", CultureInfo.InvariantCulture);
    }

    /// <summary>The cell styles whose number format shows a date.</summary>
    private static HashSet<int> DateStyles(XDocument? styles)
    {
        var result = new HashSet<int>();
        if (styles?.Root is not { } root)
        {
            return result;
        }

        var custom = new Dictionary<int, string>();
        foreach (var format in root.Element(Main + "numFmts")?.Elements(Main + "numFmt") ?? [])
        {
            if ((int?)format.Attribute("numFmtId") is { } id)
            {
                custom[id] = (string?)format.Attribute("formatCode") ?? string.Empty;
            }
        }

        var position = 0;
        foreach (var style in root.Element(Main + "cellXfs")?.Elements(Main + "xf") ?? [])
        {
            var id = (int?)style.Attribute("numFmtId") ?? 0;
            if (id is >= 14 and <= 17 or 22 || (custom.TryGetValue(id, out var code) && IsDateFormat(code)))
            {
                result.Add(position);
            }

            position++;
        }

        return result;
    }

    /// <summary>A year or day letter outside quoted text, escapes and [colour] blocks.</summary>
    private static bool IsDateFormat(string code)
    {
        var quoted = false;
        var bracketed = false;
        for (var i = 0; i < code.Length; i++)
        {
            var c = char.ToLowerInvariant(code[i]);
            if (quoted)
            {
                quoted = c != '"';
            }
            else if (bracketed)
            {
                bracketed = c != ']';
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == '[')
            {
                bracketed = true;
            }
            else if (c is '\\' or '_' or '*')
            {
                i++;
            }
            else if (c is 'y' or 'd')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The text of a shared or inline string: plain, or the joined runs of formatted text (not phonetic hints).</summary>
    private static string Text(XElement item)
    {
        var text = new StringBuilder();
        foreach (var child in item.Elements())
        {
            if (child.Name == Main + "t")
            {
                text.Append(child.Value);
            }
            else if (child.Name == Main + "r")
            {
                text.Append((string?)child.Element(Main + "t"));
            }
        }

        return text.ToString();
    }

    /// <summary>0 for "A1", 26 for "AA1"; null when there are no letters.</summary>
    private static int? ColumnOf(string? reference)
    {
        var column = 0;
        foreach (var c in reference ?? string.Empty)
        {
            if (!char.IsAsciiLetterUpper(c))
            {
                break;
            }

            column = Math.Min(column * 26 + (c - 'A' + 1), 1_000_000);
        }

        return column == 0 ? null : column - 1;
    }

    private static XDocument? Load(ZipArchive zip, string path, ref long budget)
    {
        if (zip.GetEntry(path) is not { } entry)
        {
            return null;
        }

        // The declared size can lie, so the bytes are counted as they are unzipped.
        using var source = entry.Open();
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            budget -= read;
            if (budget < 0)
            {
                throw new ImportFileException("This workbook is too large to read. Split it and try again.");
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        using var reader = XmlReader.Create(buffer, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader);
    }
}
