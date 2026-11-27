using LekhaBook.Application.Import;
using LekhaBook.Contracts;
using LekhaBook.Domain;

namespace LekhaBook.Application.Tests;

public class ImportTests
{
    private static readonly Guid Cash = Guid.NewGuid();
    private static readonly Guid Bank = Guid.NewGuid();
    private static readonly Guid Ram = Guid.NewGuid();
    private static readonly Guid Fuel = Guid.NewGuid();
    private static readonly Guid General = Guid.NewGuid();
    private static readonly Guid Commission = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 11, 26);

    private static ImportBooks Books(params TransactionDto[] entries) => new(
        [new PartyDto(Ram, "Ram Traders", "Customer", null, null, null, null, null, null, 0, "ToReceive", null, 0, DateTime.UtcNow)],
        [
            new MoneyAccountDto(Cash, "Cash", "Cash", null, null, 0, null, true, 0),
            new MoneyAccountDto(Bank, "Nabil Bank", "Bank", null, null, 0, null, false, 0),
        ],
        [new CategoryDto(Fuel, "Fuel", "Expense"), new CategoryDto(General, "General", "Expense"), new CategoryDto(Commission, "Commission", "Income")],
        entries,
        Today);

    private static TransactionDto Existing(TransactionType type, DateOnly date, decimal amount, Guid from, Guid to) => new(
        Guid.NewGuid(), Guid.NewGuid(), type.ToString(), "1", date, amount, from, to, null, null, null, "Posted", Guid.NewGuid(), DateTime.UtcNow);

    /// <summary>Plans a table whose columns are found from its first row, as the screen does.</summary>
    private static ImportPlan Plan(
        ImportKind kind, string[][] rows, ImportBooks? books = null, Func<ImportOptions, ImportOptions>? change = null)
    {
        var header = ImportColumns.DetectHeaderRow(rows, kind);
        var options = new ImportOptions
        {
            Kind = kind,
            HeaderRow = header,
            Columns = ImportColumns.Guess(rows[header], kind),
            DefaultMoneyAccountId = Cash,
            DefaultExpenseId = General,
            DefaultIncomeId = Commission,
        };
        return ImportPlanner.Build(rows, change?.Invoke(options) ?? options, books ?? Books());
    }

    [Theory]
    [InlineData("1,00,000.00", 100000, false, null)]
    [InlineData("1,000,000.50", 1000000.50, false, null)]
    [InlineData("Rs. 500/-", 500, false, null)]
    [InlineData("रू १,२५०", 1250, false, null)]
    [InlineData("(250.75)", 250.75, true, null)]
    [InlineData("-40", 40, true, null)]
    [InlineData("1200 Dr", 1200, false, 'D')]
    [InlineData("1,200.00Cr.", 1200, false, 'C')]
    public void Reads_amounts_as_other_apps_write_them(string text, double value, bool negative, char? drCr)
    {
        Assert.True(ImportValues.TryAmount(text, out var amount));
        Assert.Equal(new ImportAmount((decimal)value, negative, drCr), amount);
    }

    [Theory]
    [InlineData("12,5")] // a decimal comma must not become 125
    [InlineData("1.234,56")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("12 items")]
    public void Refuses_text_that_isnt_an_amount(string text) => Assert.False(ImportValues.TryAmount(text, out _));

    [Theory]
    [InlineData("2026-11-26", "2026-11-26")]
    [InlineData("26/11/2026", "2026-11-26")]
    [InlineData("26 Nov 2026", "2026-11-26")]
    [InlineData("26-Nov-26", "2026-11-26")]
    [InlineData("November 26, 2026", "2026-11-26")]
    [InlineData("2026-11-26T10:15:00", "2026-11-26")]
    [InlineData("2083/08/10", "2026-11-26")] // Bikram Sambat, by its year
    [InlineData("२०८३-०८-१०", "2026-11-26")]
    [InlineData("10 Mangsir 2083", "2026-11-26")]
    public void Reads_ad_and_bs_dates(string text, string expected)
    {
        Assert.True(ImportValues.TryDate(text, ImportCalendar.Auto, ImportDateOrder.DayFirst, out var date));
        Assert.Equal(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), date);
    }

    [Theory]
    [InlineData("2095/01/01")] // beyond the calendar table: refused, never shifted
    [InlineData("31/02/2026")]
    [InlineData("soon")]
    public void Refuses_dates_it_cannot_place(string text) =>
        Assert.False(ImportValues.TryDate(text, ImportCalendar.Auto, ImportDateOrder.DayFirst, out _));

    [Fact]
    public void Works_out_month_first_dates_from_the_whole_column()
    {
        Assert.Equal(ImportDateOrder.MonthFirst, ImportValues.DetectOrder(["01/02/2026", "11/26/2026"]));
        Assert.Equal(ImportDateOrder.DayFirst, ImportValues.DetectOrder(["01/02/2026"]));
    }

    [Fact]
    public void Finds_the_heading_row_under_a_title()
    {
        string[][] rows = [["My Shop", "Party list"], ["S.N.", "Party Name", "Mobile No.", "To Receive", "To Give"], ["1", "A", "98", "5", ""]];

        Assert.Equal(1, ImportColumns.DetectHeaderRow(rows, ImportKind.Parties));
        Assert.Equal(
            [ImportField.Skip, ImportField.Name, ImportField.Phone, ImportField.ToReceive, ImportField.ToGive],
            ImportColumns.Guess(rows[1], ImportKind.Parties));
    }

    [Fact]
    public void Parties_get_their_opening_balance_and_side_and_known_names_are_not_added_again()
    {
        var plan = Plan(ImportKind.Parties,
        [
            ["Name", "Phone", "Balance", "Party Type"],
            ["Sita Store", "9800000000.0", "-1,500.00", "Supplier"],
            ["Hari", "", "250 Dr", ""],
            ["Gita", "", "400 To Give", ""],
            ["ram  traders", "", "10", ""],
            ["Hari", "", "", ""],
            ["Total", "", "260", ""],
            ["Bad", "", "many", ""],
        ]);

        var sita = plan.Rows[0].Party!;
        Assert.Equal(("Supplier", 1500m, "ToGive", "9800000000"), (sita.PartyType, sita.OpeningBalance, sita.OpeningSide, sita.Phone));
        Assert.Equal(("Customer", 250m, "ToReceive"), (plan.Rows[1].Party!.PartyType, plan.Rows[1].Party!.OpeningBalance, plan.Rows[1].Party!.OpeningSide));
        Assert.Equal((400m, "ToGive"), (plan.Rows[2].Party!.OpeningBalance, plan.Rows[2].Party!.OpeningSide));
        Assert.Equal(
            [ImportRowStatus.Ready, ImportRowStatus.Ready, ImportRowStatus.Ready, ImportRowStatus.Duplicate,
             ImportRowStatus.Duplicate, ImportRowStatus.Skipped, ImportRowStatus.Error],
            plan.Rows.Select(r => r.Status));
        Assert.Equal(3, plan.Rows.Count(r => r.Selected));
    }

    [Fact]
    public void Entries_are_posted_between_the_right_accounts()
    {
        var plan = Plan(ImportKind.Entries,
        [
            ["Date", "Type", "Party Name", "Amount", "Payment Method", "Category", "Remarks"],
            ["2083/08/01", "Payment In #7", "Ram Traders", "1,000", "Cash", "", "advance"],
            ["2083/08/02", "Payment Out", "New Supplier", "500", "Nabil Bank", "", ""],
            ["2083/08/03", "Expense", "", "300", "", "Fuel", ""],
            ["2083/08/03", "Expense", "", "90", "", "Tea", "office"],
            ["2083/08/04", "Income", "", "75", "", "Commission", ""],
            ["2083/08/05", "Sales", "Ram Traders", "900", "", "", ""],
            ["2083/08/06", "Barter", "Ram Traders", "900", "", "", ""],
            ["2083/08/07", "Payment In", "Ram Traders", "12.345", "Cash", "", ""],
            ["2083/09/29", "Payment In", "Ram Traders", "5", "Cash", "", ""],
            ["2083/08/08", "Payment In", "Ram Traders", "5", "Khalti", "", ""],
            ["", "", "Total", "2,000", "", "", ""],
        ]);

        var rows = plan.Rows;
        Assert.Equal((Ram, Cash), (rows[0].Entry!.FromAccountId, rows[0].Entry!.ToAccountId));
        var supplier = Assert.Single(plan.NewParties);
        Assert.Equal(("New Supplier", (Guid?)supplier.Id), (supplier.Name, rows[1].NewPartyId));
        Assert.Equal((Bank, supplier.Id!.Value), (rows[1].Entry!.FromAccountId, rows[1].Entry!.ToAccountId));
        Assert.Equal((Cash, Fuel), (rows[2].Entry!.FromAccountId, rows[2].Entry!.ToAccountId));
        // A category the books don't have falls back to the default, and its name is kept.
        Assert.Equal((General, "Tea · office"), (rows[3].Entry!.ToAccountId, rows[3].Entry!.Remarks));
        Assert.Equal((Commission, Cash), (rows[4].Entry!.FromAccountId, rows[4].Entry!.ToAccountId));
        Assert.Equal(("Import.Row.Unsupported", ImportRowStatus.Skipped), (rows[5].Note!.Key, rows[5].Status));
        Assert.Equal("Import.Row.UnknownType", rows[6].Note!.Key);
        Assert.Equal("Import.Row.BadAmount", rows[7].Note!.Key);
        Assert.Equal("Import.Row.FutureDate", rows[8].Note!.Key);
        Assert.Equal("Import.Row.NoAccountNamed", rows[9].Note!.Key);
        Assert.Equal(ImportRowStatus.Skipped, rows[10].Status);
        Assert.Equal(["Sales", "Barter"], plan.UnknownTypes);
    }

    [Fact]
    public void The_user_can_say_what_an_unknown_type_means_or_leave_it_out()
    {
        var plan = Plan(ImportKind.Entries,
            [["Date", "Type", "Party", "Amount"], ["2026-11-01", "Barter", "Ram Traders", "9"], ["2026-11-01", "Gift", "Ram Traders", "9"]],
            change: o => o with { TypeChoices = new Dictionary<string, TransactionType?> { ["Barter"] = TransactionType.PaymentIn, ["Gift"] = null } });

        Assert.Equal(nameof(TransactionType.PaymentIn), plan.Rows[0].Entry!.Type);
        Assert.Equal(ImportRowStatus.Skipped, plan.Rows[1].Status);
    }

    [Fact]
    public void Entries_already_in_the_books_are_matched_one_for_one()
    {
        var date = new DateOnly(2026, 11, 1);
        var plan = Plan(ImportKind.Entries,
            [["Date", "Type", "Party", "Amount"], ["2026-11-01", "Payment In", "Ram Traders", "100"], ["2026-11-01", "Payment In", "Ram Traders", "100"]],
            Books(Existing(TransactionType.PaymentIn, date, 100, Ram, Cash)));

        Assert.Equal([ImportRowStatus.Duplicate, ImportRowStatus.Ready], plan.Rows.Select(r => r.Status));
        Assert.Equal([false, true], plan.Rows.Select(r => r.Selected));
    }

    [Fact]
    public void A_party_statement_turns_debit_into_money_given_and_credit_into_money_got()
    {
        var plan = Plan(ImportKind.Statement,
        [
            ["Ram Traders", "Statement"],
            ["Date", "Particulars", "Details", "Debit", "Credit", "Balance"],
            ["2083/08/01", "Opening balance", "", "", "", "100"],
            ["2083/08/02", "Payment Out #3", "Cash", "50", "", "150"],
            ["2083/08/03", "Payment In", "Cash", "", "20", "130"],
        ], change: o => o with { StatementAccountId = Ram });

        Assert.Equal(ImportRowStatus.Skipped, plan.Rows[0].Status);
        Assert.Equal((nameof(TransactionType.PaymentOut), Cash, Ram), (plan.Rows[1].Entry!.Type, plan.Rows[1].Entry!.FromAccountId, plan.Rows[1].Entry!.ToAccountId));
        Assert.Equal((nameof(TransactionType.PaymentIn), Ram, Cash), (plan.Rows[2].Entry!.Type, plan.Rows[2].Entry!.FromAccountId, plan.Rows[2].Entry!.ToAccountId));
    }

    /// <summary>The layout of the statement CSV this app's report links download (D24).</summary>
    [Fact]
    public void This_apps_own_party_statement_csv_reads_back()
    {
        var plan = Plan(ImportKind.Statement,
        [
            ["Test Kirana", "Ram Traders"],
            ["Date", "Particulars", "Details", "Cheque no.", "Gave / Added", "Got / Reduced", "Balance"],
            ["", "Opening balance", "", "", "", "", "100.00"],
            ["2083/08/02", "Payment Out #3", "Cash · advance", "", "50.00", "", "150.00"],
            ["2083/08/03", "Payment In", "Cash", "00123", "", "20.00", "130.00"],
            ["2083/08/04", "Balance added", "", "", "5.00", "", "135.00"],
        ], change: o => o with
        {
            StatementAccountId = Ram,
            TypeNames = new Dictionary<string, TransactionType> { [ImportValues.Normalize("Balance added")] = TransactionType.PartyAdjustAdd },
        });

        Assert.Equal(
            [ImportRowStatus.Skipped, ImportRowStatus.Ready, ImportRowStatus.Ready, ImportRowStatus.Skipped],
            plan.Rows.Select(r => r.Status));
        Assert.Equal((50m, "Cash · advance"), (plan.Rows[1].Entry!.Amount, plan.Rows[1].Entry!.Remarks));
        Assert.Equal((20m, "00123"), (plan.Rows[2].Entry!.Amount, plan.Rows[2].Entry!.ChequeNo));
    }

    [Fact]
    public void A_bank_statement_posts_withdrawals_and_deposits_against_the_chosen_account()
    {
        var date = new DateOnly(2026, 11, 3);
        var plan = Plan(ImportKind.Statement,
        [
            ["Txn Date", "Description", "Withdrawal", "Deposit", "Balance"],
            ["01/11/2026", "ATM", "500.00", "", "9,500.00"],
            ["02/11/2026", "Interest", "-", "25.00", "9,525.00"],
            ["03/11/2026", "Already typed in", "40", "", "9,485.00"],
            ["", "continued description", "", "", ""],
        ], Books(Existing(TransactionType.Expense, date, 40, Bank, Fuel)), o => o with { StatementAccountId = Bank });

        Assert.Equal((nameof(TransactionType.Expense), Bank, General, "ATM"),
            (plan.Rows[0].Entry!.Type, plan.Rows[0].Entry!.FromAccountId, plan.Rows[0].Entry!.ToAccountId, plan.Rows[0].Entry!.Remarks));
        Assert.Equal((nameof(TransactionType.OtherIncome), Commission, Bank),
            (plan.Rows[1].Entry!.Type, plan.Rows[1].Entry!.FromAccountId, plan.Rows[1].Entry!.ToAccountId));
        Assert.Equal(ImportRowStatus.Duplicate, plan.Rows[2].Status); // entered by hand under another category
        Assert.Equal(ImportRowStatus.Skipped, plan.Rows[3].Status);
        Assert.Empty(plan.NewParties);
    }

    [Theory]
    [InlineData(ImportKind.Entries)]
    [InlineData(ImportKind.Statement)]
    public void A_chosen_default_type_never_reverses_the_direction_the_file_shows(ImportKind kind)
    {
        var plan = Plan(kind,
            [["Date", "Debit", "Credit", "Category"], ["2026-11-01", "10", "", ""], ["2026-11-02", "", "20", ""]],
            change: o => o with { DefaultType = TransactionType.Expense, StatementAccountId = Cash });

        Assert.Equal([nameof(TransactionType.Expense), nameof(TransactionType.OtherIncome)], plan.Rows.Select(r => r.Entry!.Type));
    }

    [Fact]
    public void A_signed_amount_column_gives_the_direction()
    {
        var plan = Plan(ImportKind.Statement,
            [["Date", "Narration", "Amount"], ["2026-11-01", "in", "100"], ["2026-11-02", "out", "-30"]],
            change: o => o with { StatementAccountId = Cash });

        Assert.Equal([nameof(TransactionType.OtherIncome), nameof(TransactionType.Expense)], plan.Rows.Select(r => r.Entry!.Type));
    }

    [Fact]
    public async Task Saving_adds_a_new_party_once_and_a_retry_saves_only_what_is_left()
    {
        var plan = Plan(ImportKind.Entries,
        [
            ["Date", "Type", "Party", "Amount"],
            ["2026-11-01", "Payment In", "Hari", "1"],
            ["2026-11-02", "Payment In", "Hari", "2"],
            ["2026-11-03", "Payment In", "Hari", "3"],
        ]);
        var parties = new List<Guid>();
        var entries = new List<Guid>();
        var offline = true;

        Task<ImportReply> AddEntry(CreateTransactionRequest r)
        {
            if (offline && r.Amount == 2)
            {
                return Task.FromResult(new ImportReply("offline", Stop: true));
            }

            entries.Add(r.Id!.Value);
            return Task.FromResult(new ImportReply());
        }

        Task<ImportReply> AddParty(SavePartyRequest r)
        {
            parties.Add(r.Id!.Value);
            return Task.FromResult(new ImportReply());
        }

        Assert.False(await ImportRunner.RunAsync(plan, AddParty, AddEntry, () => { }));
        offline = false;
        Assert.True(await ImportRunner.RunAsync(plan, AddParty, AddEntry, () => { }));

        Assert.Single(parties);
        Assert.Equal(plan.Rows.Select(r => r.Entry!.Id!.Value), entries);
        Assert.All(plan.Rows, r => Assert.True(r.Saved));
    }

    [Fact]
    public async Task A_row_whose_new_party_is_refused_reports_why_and_is_not_posted()
    {
        var plan = Plan(ImportKind.Entries, [["Date", "Type", "Party", "Amount"], ["2026-11-01", "Payment In", "Hari", "1"]]);

        await ImportRunner.RunAsync(
            plan,
            _ => Task.FromResult(new ImportReply("not allowed")),
            _ => throw new InvalidOperationException("must not be posted"),
            () => { });

        Assert.Equal(("not allowed", false), (plan.Rows[0].SaveError, plan.Rows[0].Saved));
    }
}
