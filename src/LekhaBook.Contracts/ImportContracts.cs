namespace LekhaBook.Contracts;

// Importing data from other apps (D26). The server only reads a file into plain tables; nothing is saved by that
// call. The screen maps columns, shows what each row will become, and saves through the normal party and entry calls.

/// <summary>One sheet, table or document from an imported file. Every cell is text; rows may differ in length.</summary>
public sealed record ImportTableDto(string Name, IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>
/// What was read from a file. <paramref name="Format"/> is "csv", "xlsx", "html" or "pdf".
/// <paramref name="Truncated"/> is true when rows or tables beyond the limits were left out.
/// </summary>
public sealed record ImportFileDto(string Format, IReadOnlyList<ImportTableDto> Tables, bool Truncated = false);

/// <summary>Limits shared by the screen and the server.</summary>
public static class ImportLimits
{
    public const int MaxFileBytes = 5 * 1024 * 1024;

    public const int MaxRows = 5000;

    public const int MaxColumns = 50;

    public const int MaxTables = 20;
}
