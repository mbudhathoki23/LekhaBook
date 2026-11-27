using System.IO.Compression;
using System.Text;
using LekhaBook.Api.Import;
using LekhaBook.Contracts;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace LekhaBook.Api.IntegrationTests;

/// <summary>Reading CSV, Excel, HTML and PDF files into tables (D26). No database needed.</summary>
public sealed class ImportReaderTests
{
    private static ImportFileDto Read(string text, string name = "file.csv") => FileTableReader.Read(Encoding.UTF8.GetBytes(text), name);

    private static string[] Lines(ImportTableDto table) => [.. table.Rows.Select(r => string.Join("|", r))];

    [Fact]
    public void Csv_handles_quotes_line_breaks_a_byte_order_mark_and_blank_lines()
    {
        var file = Read("﻿Date,Details,Amount\r\n2026-11-01,\"Rice, \"\"good\"\"\nsecond line\",\"1,000.00\"\r\n\r\n2026-11-02,x,5");

        Assert.Equal("csv", file.Format);
        Assert.Equal(
            ["Date|Details|Amount", "2026-11-01|Rice, \"good\"\nsecond line|1,000.00", "2026-11-02|x|5"],
            Lines(Assert.Single(file.Tables)));
    }

    [Theory]
    [InlineData("a;b;c\n1;2,5;3")]
    [InlineData("a\tb\tc\n1\t2,5\t3")]
    [InlineData("sep=|\na|b|c\n1|2,5|3")]
    public void Csv_finds_the_separator(string text) => Assert.Equal(["a|b|c", "1|2,5|3"], Lines(Read(text).Tables[0]));

    [Fact]
    public void Html_tables_are_read_even_when_the_file_is_named_xls()
    {
        var file = Read(
            """
            <html><body>
              <table><tr><td>Logo</td></tr></table>
              <table><caption>Parties</caption>
                <thead><tr><th>Name</th><th colspan="2">Phone</th><th>Balance</th></tr></thead>
                <tbody><tr><td>Ram<br>Traders</td><td>980</td><td></td><td> 1,000 <script>alert(1)</script></td></tr></tbody>
              </table>
            </body></html>
            """,
            "export.xls");

        Assert.Equal("html", file.Format);
        var table = Assert.Single(file.Tables);
        Assert.Equal("Parties", table.Name);
        Assert.Equal(["Name|Phone||Balance", "Ram Traders|980||1,000"], Lines(table));
    }

    [Fact]
    public void Deeply_nested_html_does_not_crash_the_reader()
    {
        // Enough to overflow the stack if anything walked the page by recursion.
        var nested = string.Concat(Enumerable.Repeat("<span>", 50_000));
        var html = $"<table><tr><td>a</td><td>{nested}deep</td></tr></table>";

        try
        {
            Assert.Equal(["a|deep"], Lines(Read(html, "deep.html").Tables[0]));
        }
        catch (ImportFileException)
        {
            // Refusing the file is fine too; stopping the process is not.
        }
    }

    [Fact]
    public void Excel_gives_shared_and_inline_text_plain_numbers_and_dates_from_visible_sheets()
    {
        var file = FileTableReader.Read(Workbook(), "books.xlsx");

        Assert.Equal("xlsx", file.Format);
        var table = Assert.Single(file.Tables);
        Assert.Equal("Day book", table.Name);
        Assert.Equal(["Date|Name|Amount", "2026-11-26|Ram|1234.56", "2026-11-26||0.1"], Lines(table));
    }

    [Fact]
    public void A_text_pdf_is_rebuilt_into_columns_even_where_cells_are_empty()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        string[][] rows =
        [
            ["Date", "Details", "Debit", "Credit"],
            ["2026-11-01", "ATM cash", "500.00", ""],
            ["2026-11-02", "Interest paid", "", "25.00"],
            ["2026-11-03", "Rent", "1,000.00", ""],
        ];
        page.AddText("Statement of account", 14, new PdfPoint(50, 760), font);
        for (var r = 0; r < rows.Length; r++)
        {
            for (var c = 0; c < 4; c++)
            {
                if (rows[r][c].Length > 0)
                {
                    page.AddText(rows[r][c], 10, new PdfPoint(new[] { 50, 150, 320, 420 }[c], 720 - r * 20), font);
                }
            }
        }

        var file = FileTableReader.Read(builder.Build(), "statement.pdf");

        Assert.Equal("pdf", file.Format);
        Assert.Equal(
            [.. rows.Select(r => string.Join("|", r))],
            Lines(Assert.Single(file.Tables)).Where(l => !l.Contains("Statement")).ToArray());
    }

    [Fact]
    public void A_pdf_without_text_says_scanned_pages_cannot_be_read()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4);

        var error = Assert.Throws<ImportFileException>(() => FileTableReader.Read(builder.Build(), "scan.pdf"));
        Assert.Contains("Scanned", error.Message);
    }

    [Theory]
    [InlineData(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0 }, ".xlsx")] // old Excel
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 1, 2 }, "couldn't be read")] // broken PDF
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0 }, "supported")] // a picture
    [InlineData(new byte[] { 0x50, 0x4B, 3, 4, 9 }, "couldn't be read")] // broken zip
    public void Unreadable_files_are_refused_with_a_reason(byte[] content, string reason)
    {
        var error = Assert.Throws<ImportFileException>(() => FileTableReader.Read(content, "file"));
        Assert.Contains(reason, error.Message);
    }

    [Fact]
    public void Rows_beyond_the_limit_are_left_out_and_reported()
    {
        var file = Read(string.Join('\n', Enumerable.Range(0, ImportLimits.MaxRows + 10).Select(i => $"{i},x")));

        Assert.True(file.Truncated);
        Assert.Equal(ImportLimits.MaxRows, file.Tables[0].Rows.Count);
    }

    /// <summary>A small workbook as Excel writes it: one visible sheet and one hidden one.</summary>
    private static byte[] Workbook()
    {
        const string main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string xml)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(xml);
            }

            Add("xl/workbook.xml",
                $"""<workbook xmlns="{main}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Day book" sheetId="1" r:id="rId1"/><sheet name="Secret" sheetId="2" state="hidden" r:id="rId2"/></sheets></workbook>""");
            Add("xl/_rels/workbook.xml.rels",
                """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Target="/xl/worksheets/sheet2.xml"/></Relationships>""");
            Add("xl/sharedStrings.xml",
                $"""<sst xmlns="{main}"><si><t>Date</t></si><si><t>Name</t></si><si><r><t>Amo</t></r><r><t>unt</t></r></si></sst>""");
            Add("xl/styles.xml",
                $"""<styleSheet xmlns="{main}"><numFmts><numFmt numFmtId="164" formatCode="[$-409]d\-mmm\-yy;@"/></numFmts><cellXfs><xf numFmtId="0"/><xf numFmtId="14"/><xf numFmtId="164"/><xf numFmtId="4"/></cellXfs></styleSheet>""");
            Add("xl/worksheets/sheet1.xml",
                $"""
                <worksheet xmlns="{main}"><sheetData>
                  <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="s"><v>2</v></c></row>
                  <row r="2"><c r="A2" s="1"><v>46352</v></c><c r="B2" t="inlineStr"><is><t>Ram</t></is></c><c r="C2" s="3"><v>1234.5600000000001</v></c></row>
                  <row r="3"><c r="A3" s="2"><v>46352.75</v></c><c r="C3"><v>0.1</v></c></row>
                </sheetData></worksheet>
                """);
            Add("xl/worksheets/sheet2.xml", $"""<worksheet xmlns="{main}"><sheetData><row><c t="str"><v>hidden</v></c></row></sheetData></worksheet>""");
        }

        return buffer.ToArray();
    }
}
