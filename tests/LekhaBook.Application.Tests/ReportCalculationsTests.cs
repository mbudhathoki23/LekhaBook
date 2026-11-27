using LekhaBook.Contracts;
using LekhaBook.Domain;

namespace LekhaBook.Application.Tests;

/// <summary>Report totals must agree with the domain balance rules (AGENT_PLAN phase 9).</summary>
public sealed class ReportCalculationsTests
{
    private static readonly Guid Company = Guid.CreateVersion7();

    private static DateOnly Day(int day) => new(2026, 11, day);

    [Fact]
    public void A_period_statement_opens_with_the_balance_just_before_it_and_closes_like_the_domain_ledger()
    {
        var cash = Account("Cash", AccountType.Cash, 1000m);
        var other = Account("Bank", AccountType.Bank, 0m);
        var entries = new[]
        {
            Entry(cash.Id, other.Id, 100m, Day(5)),  // out
            Entry(other.Id, cash.Id, 300m, Day(10)), // in
            Entry(cash.Id, other.Id, 50m, Day(20)),  // out
        };
        var ledger = Ledger(cash, entries);

        var view = Statements.Slice(ledger, new ReportRange(Day(10), Day(15)));

        Assert.Equal(900m, view.Opening);
        Assert.Equal(300m, view.TotalDebit);
        Assert.Equal(0m, view.TotalCredit);
        Assert.Equal(1200m, view.Closing);
        var upToEnd = entries.Where(e => e.Date <= Day(15)).ToList();
        Assert.Equal(Balances.Of(cash, upToEnd), view.Closing);

        var all = Statements.Slice(ledger, ReportRange.All);
        Assert.Equal(Balances.Of(cash, entries), all.Closing);
        Assert.Equal(1000m, all.Opening);
    }

    [Fact]
    public void Combined_cash_boxes_ignore_money_moved_between_them()
    {
        var till = Account("Cash", AccountType.Cash, 1000m);
        var petty = Account("Petty Cash", AccountType.Cash, 200m);
        var bank = Account("Bank", AccountType.Bank, 0m);
        var entries = new[]
        {
            Entry(till.Id, petty.Id, 400m, Day(3)),
            Entry(bank.Id, till.Id, 50m, Day(4)),
        };

        var view = Statements.Combine([("Cash", Ledger(till, entries)), ("Petty Cash", Ledger(petty, entries))], ReportRange.All);

        var row = Assert.Single(view.Rows);
        Assert.Equal(50m, row.Line.Debit);
        Assert.Equal(1200m, view.Opening);
        Assert.Equal(Balances.Of(till, entries) + Balances.Of(petty, entries), view.Closing);
    }

    [Fact]
    public void Entry_totals_count_only_money_earned_and_spent()
    {
        var entries = new[]
        {
            Dto("PaymentIn", 100m, Day(1)),
            Dto("OtherIncome", 20m, Day(2)),
            Dto("Expense", 30m, Day(2)),
            Dto("PaymentOut", 5m, Day(2)),
            Dto("TransferMoney", 999m, Day(2)),
            Dto("AddMoney", 999m, Day(2)),
            Dto("Expense", 7m, Day(2)) with { Status = "Void" },
        };

        var inPeriod = EntryReports.InPeriod(entries, new ReportRange(Day(2), Day(2)));

        Assert.Equal(5, inPeriod.Count);
        Assert.Equal(new FlowTotals(20m, 35m), EntryReports.Totals(inPeriod));
    }

    [Fact]
    public void Category_totals_group_expenses_by_the_head_they_are_paid_to()
    {
        var fuel = Guid.CreateVersion7();
        var food = Guid.CreateVersion7();
        var cash = Guid.CreateVersion7();
        var entries = new[]
        {
            Dto("Expense", 10m, Day(1)) with { FromAccountId = cash, ToAccountId = food },
            Dto("Expense", 40m, Day(2)) with { FromAccountId = cash, ToAccountId = fuel },
            Dto("Expense", 5m, Day(3)) with { FromAccountId = cash, ToAccountId = food },
        };

        var totals = EntryReports.ByCategory(
            entries, [new(fuel, "Fuel", "Expense"), new(food, "Food", "Expense")], TransactionType.Expense, ReportRange.All);

        Assert.Equal(["Fuel", "Food"], totals.Select(t => t.Name));
        Assert.Equal(15m, totals[1].Amount);
        Assert.Equal(2, totals[1].Count);
    }

    [Theory]
    [InlineData(PeriodPreset.Today, "2026-11-27", "2026-11-27")]
    [InlineData(PeriodPreset.ThisMonth, "2026-11-01", "2026-11-30")]
    [InlineData(PeriodPreset.LastMonth, "2026-10-01", "2026-10-31")]
    [InlineData(PeriodPreset.ThisFiscalYear, "2026-04-01", "2027-03-31")]
    public void AD_periods(PeriodPreset preset, string from, string to)
    {
        var range = ReportPeriods.For(preset, new DateOnly(2026, 11, 27), CalendarMode.AD, 4);

        Assert.Equal(new ReportRange(DateOnly.Parse(from), DateOnly.Parse(to)), range);
    }

    [Fact]
    public void BS_periods_follow_Nepali_months_and_the_Shrawan_fiscal_year()
    {
        var today = new DateOnly(2026, 11, 27); // 2083 Mangsir

        var month = ReportPeriods.For(PeriodPreset.ThisMonth, today, CalendarMode.BS, 4);
        var year = ReportPeriods.For(PeriodPreset.ThisFiscalYear, today, CalendarMode.BS, 4);

        Assert.Equal("2083/08/01", CalendarDates.Format(month.From!.Value, CalendarMode.BS));
        Assert.Equal("2083/09/01", CalendarDates.Format(month.To!.Value.AddDays(1), CalendarMode.BS));
        Assert.Equal("2083/04/01", CalendarDates.Format(year.From!.Value, CalendarMode.BS));
        Assert.Equal("2084/04/01", CalendarDates.Format(year.To!.Value.AddDays(1), CalendarMode.BS));
    }

    [Fact]
    public void Csv_guards_formulas_but_keeps_negative_numbers()
    {
        var csv = CsvText.Build([["=1+1", "-250.00", "a \"quoted\" note", null]]);

        Assert.Equal("\"'=1+1\",\"-250.00\",\"a \"\"quoted\"\" note\",\"\"\r\n", csv);
    }

    private static Account Account(string name, AccountType type, decimal opening) =>
        new() { CompanyId = Company, Name = name, Type = type, OpeningBalance = opening, OpeningSide = BalanceSide.Debit };

    private static Transaction Entry(Guid from, Guid to, decimal amount, DateOnly date) => new()
    {
        CompanyId = Company,
        Type = TransactionType.TransferMoney,
        VoucherNo = "1",
        Date = date,
        Amount = amount,
        FromAccountId = from,
        ToAccountId = to,
    };

    private static LedgerDto Ledger(Account account, IEnumerable<Transaction> entries)
    {
        var lines = Balances.Ledger(account, entries);
        return new LedgerDto(
            account.Id,
            Balances.SignedOpening(account),
            null,
            lines.Select(l => new LedgerLineDto(
                l.Transaction.Id, l.Transaction.Type.ToString(), "1", l.Transaction.Date, l.Debit, l.Credit, l.Balance,
                null, null, null, [])).ToList(),
            lines.Count > 0 ? lines[^1].Balance : Balances.SignedOpening(account));
    }

    private static TransactionDto Dto(string type, decimal amount, DateOnly date) =>
        new(Guid.CreateVersion7(), Company, type, "1", date, amount, Guid.CreateVersion7(), Guid.CreateVersion7(),
            null, null, null, "Posted", Guid.CreateVersion7(), DateTime.UtcNow);
}
