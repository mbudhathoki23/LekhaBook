using LekhaBook.Application;
using LekhaBook.Contracts;
using LekhaBook.Domain;

namespace LekhaBook.Application.Tests;

public sealed class DashboardFlowsTests
{
    private static readonly Guid Company = Guid.CreateVersion7();
    private static readonly Guid Cash = Guid.CreateVersion7();
    private static readonly Guid Bank = Guid.CreateVersion7();
    private static readonly Guid Income = Guid.CreateVersion7();
    private static readonly Guid Expense = Guid.CreateVersion7();
    private static readonly Guid Adjustment = Guid.CreateVersion7();
    private static readonly DateOnly AdToday = new(2026, 11, 26);

    [Fact]
    public void Money_in_is_payments_received_and_other_income_and_money_out_is_payments_made_and_expenses()
    {
        var customer = Party("Gita Traders", 0);
        var report = Build(
            [
                Txn("PaymentIn", new(2026, 11, 3), 1000, customer.Id, Cash),
                Txn("PaymentOut", new(2026, 11, 4), 400, Cash, customer.Id),
                Txn("OtherIncome", new(2026, 11, 5), 250, Income, Cash),
                Txn("Expense", new(2026, 11, 6), 100, Cash, Expense),
                Txn("TransferMoney", new(2026, 11, 7), 5000, Cash, Bank),
                Txn("AddMoney", new(2026, 11, 8), 300, Adjustment, Cash),
                Txn("ReduceMoney", new(2026, 11, 9), 200, Cash, Adjustment),
                Txn("PartyAdjustAdd", new(2026, 11, 10), 700, Adjustment, customer.Id),
            ],
            [customer]);

        Assert.Equal(new FlowTotals(1250m, 500m), report.ThisMonth);
        Assert.Equal(750m, report.ThisMonth.Net);
    }

    [Fact]
    public void Voided_entries_are_left_out()
    {
        var report = Build([Txn("PaymentIn", AdToday, 900, Guid.CreateVersion7(), Cash, status: "Void")], []);

        Assert.Equal(FlowTotals.Zero, report.ThisMonth);
    }

    [Fact]
    public void The_last_twelve_months_end_with_the_current_month_and_empty_months_are_zero()
    {
        var report = Build([Txn("Expense", new(2025, 11, 30), 75, Cash, Expense)], []);

        Assert.Equal(DashboardFlows.MonthCount, report.Months.Count);
        Assert.Equal((2025, 12), (report.Months[0].Year, report.Months[0].Month));
        Assert.Equal((2026, 11), (report.Months[^1].Year, report.Months[^1].Month));
        Assert.All(report.Months, m => Assert.Equal(FlowTotals.Zero, m.Totals));
    }

    [Fact]
    public void A_fiscal_year_starts_in_the_fiscal_start_month()
    {
        var report = Build(
            [
                Txn("PaymentIn", new(2026, 3, 31), 100, Guid.CreateVersion7(), Cash),
                Txn("PaymentIn", new(2026, 4, 1), 200, Guid.CreateVersion7(), Cash),
            ],
            [], fiscalYearStartMonth: 4);

        Assert.Equal(2026, report.CurrentFiscalYear);
        Assert.Equal(new FlowTotals(100m, 0m), report.FiscalYears[^2].Totals);
        Assert.Equal(new FlowTotals(200m, 0m), report.FiscalYears[^1].Totals);
        Assert.Equal(5, report.FiscalYears.Count);
    }

    [Fact]
    public void Bikram_Sambat_companies_group_by_Nepali_month()
    {
        // BS 2075 Shrawan (month 4) starts on 2018-07-17 and Bhadra (month 5) on 2018-08-17 (NepaliCalendarTests).
        var today = new DateOnly(2018, 8, 20);
        var report = Build(
            [
                Txn("PaymentIn", new(2018, 7, 17), 500, Guid.CreateVersion7(), Cash),
                Txn("OtherIncome", new(2018, 8, 17), 70, Income, Cash),
                Txn("OtherIncome", new(2018, 8, 16), 30, Income, Cash),
            ],
            [], CalendarMode.BS, fiscalYearStartMonth: 4, today);

        Assert.Equal((2075, 5), (report.Months[^1].Year, report.Months[^1].Month));
        // 2018-08-16 is the last day of Shrawan, so it joins the 500 received on 2018-07-17.
        Assert.Equal(new FlowTotals(70m, 0m), report.ThisMonth);
        Assert.Equal(new FlowTotals(530m, 0m), report.Months[^2].Totals);
        Assert.Equal(2075, report.CurrentFiscalYear);
    }

    [Fact]
    public void Dates_outside_the_Bikram_Sambat_table_are_not_placed()
    {
        var report = Build(
            [Txn("PaymentIn", new(2040, 1, 1), 999, Guid.CreateVersion7(), Cash)],
            [], CalendarMode.BS, fiscalYearStartMonth: 4, new DateOnly(2040, 1, 1));

        Assert.Empty(report.Months);
        Assert.Empty(report.TopParties);
    }

    [Fact]
    public void Top_parties_rank_by_business_this_fiscal_year_and_keep_five()
    {
        var parties = new[]
        {
            Party("Asha", 120), Party("Bikash", -40), Party("Chandra", 0),
            Party("Dipa", 15), Party("Esha", 900), Party("Farid", 5), Party("Gita", -10),
        };
        var txns = new List<TransactionDto>
        {
            Txn("PaymentIn", new(2026, 5, 1), 1000, parties[4].Id, Cash),   // Esha: 1000
            Txn("PaymentOut", new(2026, 6, 1), 200, Cash, parties[0].Id),   // Asha: 200
            Txn("PaymentIn", new(2026, 7, 1), 300, parties[1].Id, Cash),    // Bikash: 300
            Txn("PaymentIn", new(2026, 8, 1), 300, parties[2].Id, Cash),    // Chandra: 300 (ties Bikash)
            Txn("PaymentOut", new(2026, 9, 1), 50, Cash, parties[3].Id),    // Dipa: 50
            Txn("PaymentIn", new(2026, 10, 1), 20, parties[5].Id, Cash),    // Farid: 20
            Txn("PaymentIn", new(2026, 10, 2), 10, parties[6].Id, Cash),    // Gita: 10
            Txn("PaymentIn", new(2025, 5, 1), 9000, parties[0].Id, Cash),   // last fiscal year: not counted
        };

        var report = Build(txns, parties);

        Assert.Equal(5, report.TopParties.Count);
        Assert.Equal(new[] { "Esha", "Bikash", "Chandra", "Asha", "Dipa" }, report.TopParties.Select(p => p.Name).ToArray());
        var asha = report.TopParties.Single(p => p.Name == "Asha");
        Assert.Equal((0m, 200m, 120m), (asha.Received, asha.Paid, asha.Balance));
        Assert.Equal(900m, report.TopParties[0].Balance);
    }

    [Fact]
    public void Months_and_years_without_entries_are_left_out_of_the_lists()
    {
        var report = Build(
            [
                Txn("Expense", new(2026, 9, 5), 75, Cash, Expense),
                Txn("PaymentIn", new(2026, 11, 3), 300, Guid.CreateVersion7(), Cash),
            ],
            []);

        Assert.Equal(new[] { (2026, 9), (2026, 11) }, report.ActiveMonths.Select(m => (m.Year, m.Month)).ToArray());
        Assert.Equal(new[] { 2026 }, report.ActiveFiscalYears.Select(y => y.StartYear).ToArray());
        Assert.Empty(Build([], []).ActiveMonths);
        Assert.Empty(Build([], []).ActiveFiscalYears);
    }

    private static DashboardReport Build(
        IReadOnlyList<TransactionDto> transactions,
        IReadOnlyList<PartyDto> parties,
        CalendarMode calendar = CalendarMode.AD,
        int fiscalYearStartMonth = 4,
        DateOnly? today = null) =>
        DashboardFlows.Build(transactions, parties, calendar, fiscalYearStartMonth, today ?? AdToday);

    private static TransactionDto Txn(
        string type, DateOnly date, decimal amount, Guid from, Guid to, string status = "Posted") =>
        new(Guid.CreateVersion7(), Company, type, "V-1", date, amount, from, to, null, null, null, status, Guid.Empty, DateTime.UtcNow);

    private static PartyDto Party(string name, decimal balance) =>
        new(Guid.CreateVersion7(), name, "Customer", null, null, null, null, null, null, 0m, "ToReceive", null, balance, DateTime.UtcNow);
}
