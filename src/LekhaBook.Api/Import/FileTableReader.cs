using System.Text;
using LekhaBook.Contracts;

namespace LekhaBook.Api.Import;

/// <summary>A file that can't be imported, with a message for the user.</summary>
public sealed class ImportFileException(string message) : Exception(message);

/// <summary>
/// Reads a CSV/text, Excel (.xlsx), HTML or PDF file into plain tables (D26). The kind is decided by the content,
/// not the name, because many apps export HTML tables named ".xls". Files are untrusted: sizes, rows, columns
/// and unzipped sizes are capped, XML may not declare a DTD, and HTML is only parsed, never run.
/// </summary>
public static class FileTableReader
{
    private const int MaxCellLength = 2000;

    public static ImportFileDto Read(byte[] content, string fileName)
    {
        if (content.Length == 0)
        {
            throw new ImportFileException("The file is empty.");
        }

        if (content.Length > ImportLimits.MaxFileBytes)
        {
            throw new ImportFileException("File too large. Maximum 5 MB.");
        }

        var collector = new TableCollector();
        string format;
        try
        {
            if (StartsWith(content, "%PDF"u8))
            {
                format = "pdf";
                PdfTables.Read(content, collector);
            }
            else if (StartsWith(content, "PK"u8))
            {
                format = "xlsx";
                XlsxTables.Read(content, collector);
            }
            else if (StartsWith(content, [0xD0, 0xCF, 0x11, 0xE0]))
            {
                throw new ImportFileException("Old Excel (.xls) and password-protected files can't be read. Save the file as .xlsx or CSV and try again.");
            }
            else
            {
                var text = Decode(content);
                if (HtmlTables.LooksLikeHtml(text))
                {
                    format = "html";
                    HtmlTables.Read(text, collector);
                }
                else
                {
                    format = "csv";
                    CsvTables.Read(text, Path.GetFileNameWithoutExtension(fileName), collector);
                }
            }
        }
        catch (ImportFileException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Damaged or unusual files fail in many ways inside the parsers.
            throw new ImportFileException("This file couldn't be read. It may be damaged or password protected.");
        }

        if (collector.Tables.Count == 0)
        {
            throw new ImportFileException(format == "pdf"
                ? "No text was found in this PDF. Scanned pages and photos can't be imported."
                : "No table with data was found in this file.");
        }

        return new ImportFileDto(format, collector.Tables, collector.Truncated);
    }

    private static bool StartsWith(byte[] content, ReadOnlySpan<byte> prefix) => content.AsSpan().StartsWith(prefix);

    private static string Decode(byte[] content)
    {
        // A byte order mark decides; otherwise UTF-8, or Windows-1252-like Latin 1 when it isn't valid UTF-8.
        if (StartsWith(content, [0xFF, 0xFE]))
        {
            return Encoding.Unicode.GetString(content, 2, content.Length - 2);
        }

        if (StartsWith(content, [0xFE, 0xFF]))
        {
            return Encoding.BigEndianUnicode.GetString(content, 2, content.Length - 2);
        }

        var offset = StartsWith(content, [0xEF, 0xBB, 0xBF]) ? 3 : 0;
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content, offset, content.Length - offset);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(content, offset, content.Length - offset);
        }

        if (text.Contains('\0'))
        {
            throw new ImportFileException("This kind of file isn't supported. Use CSV, Excel (.xlsx), HTML or PDF.");
        }

        return text;
    }

    /// <summary>Applies the row, column, cell and table limits while the readers add what they find.</summary>
    internal sealed class TableCollector
    {
        private readonly List<ImportTableDto> _tables = [];
        private int _totalRows;

        public IReadOnlyList<ImportTableDto> Tables => _tables;

        public bool Truncated { get; private set; }

        public bool IsFull => _tables.Count >= ImportLimits.MaxTables || _totalRows >= ImportLimits.MaxRows;

        /// <summary>Adds a table, dropping blank rows. Reading may stop early once <see cref="IsFull"/>.</summary>
        public void Add(string name, IEnumerable<IReadOnlyList<string>> rows)
        {
            if (IsFull)
            {
                Truncated = true;
                return;
            }

            var kept = new List<IReadOnlyList<string>>();
            foreach (var row in rows)
            {
                if (row.All(string.IsNullOrWhiteSpace))
                {
                    continue;
                }

                if (_totalRows >= ImportLimits.MaxRows)
                {
                    Truncated = true;
                    break;
                }

                if (row.Count > ImportLimits.MaxColumns)
                {
                    Truncated = true;
                }

                kept.Add(row.Take(ImportLimits.MaxColumns).Select(Clean).ToArray());
                _totalRows++;
            }

            if (kept.Count > 0)
            {
                _tables.Add(new ImportTableDto(name, kept));
            }
        }

        private static string Clean(string? cell)
        {
            var text = cell?.Trim() ?? string.Empty;
            return text.Length <= MaxCellLength ? text : text[..MaxCellLength];
        }
    }
}
