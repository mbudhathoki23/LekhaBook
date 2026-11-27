using LekhaBook.Contracts;
using LekhaBook.Domain;

namespace LekhaBook.Application;

/// <summary>
/// Money in and out for the dashboard's monthly, yearly and top-party views (AGENT_PLAN § 9, <c>reports.dashboard</c>).
/// <para>
/// Money in is payments received from parties and other income. Money out is payments made to parties and expenses.
/// Transfers between money accounts and balance adjustments move money without earning or spending it, so they are left out.
/// </para>
/// <para>
/// Periods follow the company's calendar: Bikram Sambat months, or AD months. A fiscal year starts in
/// <c>FiscalYearStartMonth</c> of that calendar (Nepal: 4 = Shrawan). Dates outside the Bikram Sambat table are not placed.
/// </para>
/// </summary>
public static class DashboardFlows
{
    public const int MonthCount = 12;
    public const int FiscalYearCount = 5;
    public const int TopPartyCount = 5;

    /// <param name="transactions">Posted entries the user may see. Voided entries are ignored.</param>
    /// <param name="parties">Used to name the top parties and read their balances.</param>
    /// <param name="calendar">The company's calendar, which decides how months and years are grouped.</param>
    /// <param name="fiscalYearStartMonth">Month (in the company's calendar) that a fiscal year starts in.</param>
    /// <param name="today">The company's today.</param>
    public static DashboardReport Build(
        IEnumerable<TransactionDto> transactions,
        IEnumerable<PartyDto> parties,
        CalendarMode calendar,
        int fiscalYearStartMonth,
        DateOnly today)
    {
        var startMonth = fiscalYearStartMonth is >= 1 and <= 12 ? fiscalYearStartMonth : 1;
        if (!TryPeriod(today, calendar, out var now))
        {
            return DashboardReport.Empty;
        }

        var currentYear = FiscalYearOf(now, startMonth);
        var partyById = parties.ToDictionary(p => p.Id);
        var byMonth = new Dictionary<int, FlowTotals>();
        var byYear = new Dictionary<int, FlowTotals>();
        var received = new Dictionary<Guid, decimal>();
        var paid = new Dictionary<Guid, decimal>();

        foreach (var entry in transactions)
        {
            if (entry.Status != "Posted"
                || !Enum.TryParse<TransactionType>(entry.Type, out var type)
                || !TryPeriod(entry.Date, calendar, out var month))
            {
                continue;
            }

            var moneyIn = type is TransactionType.PaymentIn or TransactionType.OtherIncome ? entry.Amount : 0m;
            var moneyOut = type is TransactionType.PaymentOut or TransactionType.Expense ? entry.Amount : 0m;
            if (moneyIn != 0m || moneyOut != 0m)
            {
                var year = FiscalYearOf(month, startMonth);
                byMonth[month.Index] = byMonth.GetValueOrDefault(month.Index).Plus(moneyIn, moneyOut);
                byYear[year] = byYear.GetValueOrDefault(year).Plus(moneyIn, moneyOut);
            }

            if (FiscalYearOf(month, startMonth) != currentYear)
            {
                continue;
            }

            // A payment in is credited to the party (From); a payment out is debited to it (To).
            if (type == TransactionType.PaymentIn)
            {
                AddTo(received, entry.FromAccountId, entry.Amount);
            }
            else if (type == TransactionType.PaymentOut)
            {
                AddTo(paid, entry.ToAccountId, entry.Amount);
            }
        }

        var months = Enumerable.Range(0, MonthCount)
            .Select(i =>
            {
                var m = now.AddMonths(i - (MonthCount - 1));
                return new MonthFlow(m.Year, m.Month, byMonth.GetValueOrDefault(m.Index));
            })
            .ToList();

        var fiscalYears = Enumerable.Range(0, FiscalYearCount)
            .Select(i =>
            {
                var year = currentYear - (FiscalYearCount - 1) + i;
                return new FiscalYearFlow(year, byYear.GetValueOrDefault(year));
            })
            .ToList();

        var topParties = received.Keys.Union(paid.Keys)
            .Where(partyById.ContainsKey)
            .Select(id =>
            {
                var party = partyById[id];
                return new PartyVolume(id, party.Name, received.GetValueOrDefault(id), paid.GetValueOrDefault(id), party.Balance);
            })
            .OrderByDescending(p => p.Total)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Take(TopPartyCount)
            .ToList();

        return new DashboardReport(months, months[^1].Totals, fiscalYears, currentYear, topParties);
    }

    private static void AddTo(Dictionary<Guid, decimal> totals, Guid partyId, decimal amount) =>
        totals[partyId] = totals.GetValueOrDefault(partyId) + amount;

    private static int FiscalYearOf(CalendarMonth month, int startMonth) =>
        month.Month >= startMonth ? month.Year : month.Year - 1;

    private static bool TryPeriod(DateOnly date, CalendarMode calendar, out CalendarMonth month)
    {
        if (calendar == CalendarMode.AD)
        {
            month = new CalendarMonth(date.Year, date.Month);
            return true;
        }

        if (NepaliCalendar.TryFromAd(date, out var bs))
        {
            month = new CalendarMonth(bs.Year, bs.Month);
            return true;
        }

        month = default;
        return false;
    }
}

/// <summary>A month in the company's calendar (BS or AD).</summary>
public readonly record struct CalendarMonth(int Year, int Month)
{
    /// <summary>Months since year 0, so consecutive months differ by one.</summary>
    public int Index => Year * 12 + (Month - 1);

    public CalendarMonth AddMonths(int months)
    {
        var index = Index + months;
        return new CalendarMonth(index / 12, index % 12 + 1);
    }
}

/// <summary>Money in and money out over one period.</summary>
public readonly record struct FlowTotals(decimal MoneyIn, decimal MoneyOut)
{
    public static FlowTotals Zero { get; } = new(0m, 0m);

    public decimal Net => MoneyIn - MoneyOut;

    public FlowTotals Plus(decimal moneyIn, decimal moneyOut) => new(MoneyIn + moneyIn, MoneyOut + moneyOut);
}

public sealed record MonthFlow(int Year, int Month, FlowTotals Totals);

/// <summary><paramref name="StartYear"/> is the year the fiscal year starts in (2083 for FY 2083/84).</summary>
public sealed record FiscalYearFlow(int StartYear, FlowTotals Totals);

/// <summary>What a party did in the current fiscal year, and its balance now (positive = To Receive).</summary>
public sealed record PartyVolume(Guid PartyId, string Name, decimal Received, decimal Paid, decimal Balance)
{
    /// <summary>Business in both directions, the figure the top-party list ranks by.</summary>
    public decimal Total => Received + Paid;
}

/// <param name="Months">The last twelve months, oldest first, ending with the current month.</param>
/// <param name="ThisMonth">The current month's totals.</param>
/// <param name="FiscalYears">The last five fiscal years, oldest first, ending with the current one.</param>
/// <param name="CurrentFiscalYear">The year the current fiscal year starts in.</param>
/// <param name="TopParties">Up to five parties with the most business this fiscal year, largest first.</param>
public sealed record DashboardReport(
    IReadOnlyList<MonthFlow> Months,
    FlowTotals ThisMonth,
    IReadOnlyList<FiscalYearFlow> FiscalYears,
    int CurrentFiscalYear,
    IReadOnlyList<PartyVolume> TopParties)
{
    /// <summary>Used when today can't be placed in the company's calendar.</summary>
    public static DashboardReport Empty { get; } = new([], default, [], 0, []);

    /// <summary>The months that had money in or out, oldest first. Empty months are left out.</summary>
    public IReadOnlyList<MonthFlow> ActiveMonths => Months.Where(m => m.Totals != FlowTotals.Zero).ToList();

    /// <summary>The fiscal years that had money in or out, oldest first. Empty years are left out.</summary>
    public IReadOnlyList<FiscalYearFlow> ActiveFiscalYears => FiscalYears.Where(y => y.Totals != FlowTotals.Zero).ToList();
}
