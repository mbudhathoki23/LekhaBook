using LekhaBook.Contracts;
using LekhaBook.Domain;

namespace LekhaBook.Application.Import;

// Turning the tables read from a file into parties and entries (D26). Everything here is pure: the screen supplies
// the books as they are, gets a plan back, and saves the selected rows through the normal calls.

public enum ImportKind
{
    /// <summary>A list of customers and suppliers, with their balances.</summary>
    Parties,

    /// <summary>Entries of many parties or categories (a day book or "all transactions" report).</summary>
    Entries,

    /// <summary>The statement of one chosen party or money account (a party ledger or bank statement).</summary>
    Statement,
}

/// <summary>What a column of the file holds. <see cref="Skip"/> columns are ignored.</summary>
public enum ImportField
{
    Skip,
    Date,
    Name,
    PartyType,
    Phone,
    Email,
    Address,
    Pan,
    Balance,
    ToReceive,
    ToGive,
    Type,
    Amount,
    MoneyIn,
    MoneyOut,
    Account,
    ToAccount,
    Category,
    Remarks,
    ChequeNo,
}

public enum ImportCalendar
{
    /// <summary>A year from 2070 on is Bikram Sambat, an earlier one AD.</summary>
    Auto,
    BS,
    AD,
}

/// <summary>For dates written with the year last, such as 05/04/2026.</summary>
public enum ImportDateOrder
{
    Auto,
    DayFirst,
    MonthFirst,
}

/// <summary>The books an import is checked against.</summary>
public sealed record ImportBooks(
    IReadOnlyList<PartyDto> Parties,
    IReadOnlyList<MoneyAccountDto> Accounts,
    IReadOnlyList<CategoryDto> Categories,
    IReadOnlyList<TransactionDto> Entries,
    DateOnly Today);

/// <summary>The choices made on the import screen.</summary>
public sealed record ImportOptions
{
    public ImportKind Kind { get; init; }

    /// <summary>Index of the row with the column headings, or -1 when the file has none.</summary>
    public int HeaderRow { get; init; }

    /// <summary>One entry per column.</summary>
    public IReadOnlyList<ImportField> Columns { get; init; } = [];

    public ImportCalendar Calendar { get; init; }

    public ImportDateOrder DateOrder { get; init; }

    /// <summary>For parties whose type the file doesn't give.</summary>
    public PartyType DefaultPartyType { get; init; } = PartyType.Customer;

    /// <summary>Statement imports: the party or money account the statement belongs to.</summary>
    public Guid? StatementAccountId { get; init; }

    /// <summary>Used when a row names no payment method.</summary>
    public Guid? DefaultMoneyAccountId { get; init; }

    public Guid? DefaultExpenseId { get; init; }

    public Guid? DefaultIncomeId { get; init; }

    public bool AddMissingParties { get; init; } = true;

    /// <summary>For files that write money in where this app expects money out.</summary>
    public bool SwapInOut { get; init; }

    /// <summary>The type of rows when the file has no type column; null works it out from each row.</summary>
    public TransactionType? DefaultType { get; init; }

    /// <summary>The user's choice for a word in the type column; a null value leaves those rows out.</summary>
    public IReadOnlyDictionary<string, TransactionType?> TypeChoices { get; init; } = new Dictionary<string, TransactionType?>();

    /// <summary>More words for the type column, such as this app's own type names in the screen's language.</summary>
    public IReadOnlyDictionary<string, TransactionType> TypeNames { get; init; } = new Dictionary<string, TransactionType>();

    /// <summary>Words the form checks in; English when null.</summary>
    public ValidationText? Text { get; init; }
}

public enum ImportRowStatus
{
    Ready,

    /// <summary>Looks like something already in the books; left unselected.</summary>
    Duplicate,

    /// <summary>Not something to import (a total, a blank line, an unsupported kind).</summary>
    Skipped,

    Error,
}

/// <summary>Why a row isn't simply ready: a resource key ("Import.Row.…") and its arguments.</summary>
public sealed record ImportNote(string Key, params object[] Args);

/// <summary>One row of the file and what it would become.</summary>
public sealed class ImportRow
{
    /// <summary>The row's number in the file, from 1.</summary>
    public int Line { get; init; }

    public ImportRowStatus Status { get; set; } = ImportRowStatus.Ready;

    public ImportNote? Note { get; set; }

    public bool Selected { get; set; }

    public bool CanSelect => Status is ImportRowStatus.Ready or ImportRowStatus.Duplicate;

    public SavePartyRequest? Party { get; set; }

    public CreateTransactionRequest? Entry { get; set; }

    /// <summary>A party that has to be added before this entry; one of <see cref="ImportPlan.NewParties"/>.</summary>
    public Guid? NewPartyId { get; set; }

    // ---- What the preview shows ----
    public DateOnly? Date { get; set; }

    public decimal? Amount { get; set; }

    public TransactionType? Type { get; set; }

    /// <summary>The party's name, or the accounts an entry moves between.</summary>
    public string Title { get; set; } = string.Empty;

    public string? Detail { get; set; }

    // ---- Set while saving ----
    public bool Saved { get; set; }

    public string? SaveError { get; set; }
}

public sealed record ImportPlan(
    IReadOnlyList<ImportRow> Rows,
    IReadOnlyList<SavePartyRequest> NewParties,
    IReadOnlyList<string> UnknownTypes)
{
    public static readonly ImportPlan Empty = new([], [], []);
}
