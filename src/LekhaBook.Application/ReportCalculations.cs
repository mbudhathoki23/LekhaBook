using System.Globalization;
using System.Text;
using LekhaBook.Contracts;
using LekhaBook.Domain;

namespace LekhaBook.Application;

// The calculations behind the report screens (AGENT_PLAN § 9, decision on the report screens in DECISIONS.md).
// Pure: they take what the API returned and never store a balance.

public enum PeriodPreset
{
    Today,
    ThisMonth,
    LastMonth,
    ThisFiscalYear,
    AllTime,
    Custom,
}

/// <summary>An inclusive span of AD dates; a null end is open.</summary>
public readonly record struct ReportRange(DateOnly? From, DateOnly? To)
{
    public static ReportRange All => new(null, null);

    public bool Contains(DateOnly date) => (From is not { } from || date >= from) && (To is not { } to || date <= to);
}

public static class ReportPeriods
{
    /// <summary>
    /// The dates a preset covers, in the company's calendar. <paramref name="today"/> is the company's today.
    /// A date the Nepali calendar table doesn't cover falls back to AD months. <see cref="PeriodPreset.Custom"/> and
    /// <see cref="PeriodPreset.AllTime"/> are open.
    /// </summary>
    public static ReportRange For(PeriodPreset preset, DateOnly today, CalendarMode calendar, int fiscalYearStartMonth)
    {
        if (preset == PeriodPreset.Today)
        {
            return new(today, today);
        }

        if (preset is PeriodPreset.AllTime or PeriodPreset.Custom)
        {
            return ReportRange.All;
        }

        var start = fiscalYearStartMonth is >= 1 and <= 12 ? fiscalYearStartMonth : 1;
        if (calendar == CalendarMode.BS && NepaliCalendar.TryFromAd(today, out var bs))
        {
            var (year, month) = (bs.Year, bs.Month);
            if (preset == PeriodPreset.LastMonth)
            {
                (year, month) = month == 1 ? (year - 1, 12) : (year, month - 1);
            }

            if (preset == PeriodPreset.ThisFiscalYear)
            {
                var first = month >= start ? year : year - 1;
                var nextStart = BsMonthStart(first + 1, start);
                return new(BsMonthStart(first, start), nextStart?.AddDays(-1));
            }

            var from = BsMonthStart(year, month);
            var next = month == 12 ? BsMonthStart(year + 1, 1) : BsMonthStart(year, month + 1);
            return new(from, next?.AddDays(-1));
        }

        if (preset == PeriodPreset.ThisFiscalYear)
        {
            var firstDay = new DateOnly(today.Month >= start ? today.Year : today.Year - 1, start, 1);
            return new(firstDay, firstDay.AddYears(1).AddDays(-1));
        }

        var monthStart = new DateOnly(today.Year, today.Month, 1);
        if (preset == PeriodPreset.LastMonth)
        {
            monthStart = monthStart.AddMonths(-1);
        }

        return new(monthStart, monthStart.AddMonths(1).AddDays(-1));
    }

    private static DateOnly? BsMonthStart(int year, int month) =>
        NepaliCalendar.TryToAd(new NepaliDate(year, month, 1), out var date) ? date : null;
}

/// <summary>One line of a statement over a period. <paramref name="Account"/> is set when several accounts are combined.</summary>
public sealed record StatementRow(LedgerLineDto Line, decimal Balance, string? Account = null);

public sealed record StatementView(decimal Opening, IReadOnlyList<StatementRow> Rows, decimal TotalDebit, decimal TotalCredit, decimal Closing);

public static class Statements
{
    /// <summary>
    /// The part of a ledger inside <paramref name="range"/>. The opening figure is the balance just before the start:
    /// the account's opening plus every earlier line.
    /// </summary>
    public static StatementView Slice(LedgerDto ledger, ReportRange range)
    {
        var opening = OpeningAt(ledger, range.From);
        var rows = ledger.Lines.Where(l => range.Contains(l.Date)).Select(l => new StatementRow(l, 0)).ToList();
        return Run(opening, rows);
    }

    /// <summary>
    /// Several accounts as one (for example all cash boxes). Moving money between two of them changes nothing,
    /// so such an entry is left out.
    /// </summary>
    public static StatementView Combine(IReadOnlyList<(string Account, LedgerDto Ledger)> ledgers, ReportRange range)
    {
        var opening = ledgers.Sum(l => OpeningAt(l.Ledger, range.From));
        var rows = ledgers
            .SelectMany(l => l.Ledger.Lines.Where(x => range.Contains(x.Date)).Select(x => new StatementRow(x, 0, l.Account)))
            .GroupBy(r => r.Line.TransactionId)
            .Where(g => g.Sum(r => r.Line.Debit - r.Line.Credit) != 0)
            .Select(g => g.First())
            .OrderBy(r => r.Line.Date)
            .ToList();
        return Run(opening, rows);
    }

    private static decimal OpeningAt(LedgerDto ledger, DateOnly? from) =>
        ledger.OpeningBalance + (from is { } start ? ledger.Lines.Where(l => l.Date < start).Sum(l => l.Debit - l.Credit) : 0m);

    private static StatementView Run(decimal opening, List<StatementRow> rows)
    {
        var balance = opening;
        for (var i = 0; i < rows.Count; i++)
        {
            balance += rows[i].Line.Debit - rows[i].Line.Credit;
            rows[i] = rows[i] with { Balance = balance };
        }

        return new StatementView(opening, rows, rows.Sum(r => r.Line.Debit), rows.Sum(r => r.Line.Credit), balance);
    }
}

public static class EntryReports
{
    /// <summary>Money earned or collected: the same definition the dashboard uses.</summary>
    public static bool IsMoneyIn(string type) =>
        type is nameof(TransactionType.PaymentIn) or nameof(TransactionType.OtherIncome);

    /// <summary>Money spent or paid out. Transfers and adjustments are neither in nor out.</summary>
    public static bool IsMoneyOut(string type) =>
        type is nameof(TransactionType.PaymentOut) or nameof(TransactionType.Expense);

    /// <summary>Posted entries inside the period, newest first.</summary>
    public static List<TransactionDto> InPeriod(IEnumerable<TransactionDto> entries, ReportRange range) =>
        entries.Where(e => e.Status == "Posted" && range.Contains(e.Date))
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.CreatedAtUtc)
            .ToList();

    public static FlowTotals Totals(IEnumerable<TransactionDto> entries) =>
        entries.Aggregate(default(FlowTotals), (total, e) =>
            total.Plus(IsMoneyIn(e.Type) ? e.Amount : 0m, IsMoneyOut(e.Type) ? e.Amount : 0m));

    /// <summary>The head an expense or income entry belongs to: an expense is debited to it, income credited from it.</summary>
    public static Guid CategoryOf(TransactionDto entry) =>
        entry.Type == nameof(TransactionType.Expense) ? entry.ToAccountId : entry.FromAccountId;

    /// <summary>Totals per head for <paramref name="type"/> (Expense or OtherIncome) in the period, largest first.</summary>
    public static List<CategoryTotal> ByCategory(
        IEnumerable<TransactionDto> entries, IEnumerable<CategoryDto> categories, TransactionType type, ReportRange range)
    {
        var names = categories.ToDictionary(c => c.Id, c => c.Name);
        return InPeriod(entries, range)
            .Where(e => e.Type == type.ToString())
            .GroupBy(CategoryOf)
            .Select(g => new CategoryTotal(g.Key, names.GetValueOrDefault(g.Key) ?? "—", g.Count(), g.Sum(e => e.Amount)))
            .OrderByDescending(c => c.Amount).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}

public sealed record CategoryTotal(Guid CategoryId, string Name, int Count, decimal Amount);

/// <summary>What each party owes or is owed.</summary>
public static class PartyReports
{
    public static decimal Receivable(PartyDto party) => party.Balance > 0 ? party.Balance : 0m;

    public static decimal Payable(PartyDto party) => party.Balance < 0 ? -party.Balance : 0m;

    /// <param name="type">"Customer", "Supplier" or null for both.</param>
    /// <param name="due">"receive", "give" or null for every balance.</param>
    public static List<PartyDto> Filter(IEnumerable<PartyDto> parties, string? type, string? due, Guid? category) =>
        parties.Where(p => (type is null || p.PartyType == type)
                           && (category is null || p.CategoryId == category)
                           && due switch { "receive" => p.Balance > 0, "give" => p.Balance < 0, _ => true })
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}

/// <summary>Comma-separated text a spreadsheet can open safely.</summary>
public static class CsvText
{
    /// <summary>
    /// Quotes a cell. Text starting like a formula gets a leading apostrophe, so a remark can't run in a
    /// spreadsheet; plain negative numbers are left as numbers.
    /// </summary>
    public static string Cell(string? cell)
    {
        var text = cell ?? string.Empty;
        if (text.Length > 0 && "=+-@\t\r".Contains(text[0])
            && !decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        {
            text = "'" + text;
        }

        return $"\"{text.Replace("\"", "\"\"")}\"";
    }

    public static string Build(IEnumerable<string?[]> rows)
    {
        var csv = new StringBuilder();
        foreach (var row in rows)
        {
            csv.AppendJoin(',', row.Select(Cell)).Append("\r\n");
        }

        return csv.ToString();
    }

    public static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
