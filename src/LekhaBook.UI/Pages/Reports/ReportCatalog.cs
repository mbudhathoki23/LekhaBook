using LekhaBook.Domain;
using LekhaBook.UI.Services;
using MudBlazor;

namespace LekhaBook.UI.Pages.Reports;

/// <summary>One report screen: where it is, what it is called and who may open it.</summary>
/// <param name="Key">Resource keys are <c>Reports.{Key}</c> and <c>Reports.{Key}.Help</c>.</param>
/// <param name="Permission">The <c>reports.*</c> grant (AGENT_PLAN § 4.2).</param>
/// <param name="Needs">The data rights the report reads; any one of them is enough.</param>
public sealed record ReportLink(string Group, string Key, string Href, string Icon, string Permission, IReadOnlyList<string> Needs)
{
    public bool IsAllowed(ISessionService session) =>
        session.Can(Permission) && (Needs.Count == 0 || Needs.Any(session.Can));
}

/// <summary>The v1 report list (docs/discovery/05-reports.md).</summary>
public static class ReportCatalog
{
    private static readonly string[] Parties = [Domain.Permission.CustomersView, Domain.Permission.SuppliersView];

    private static readonly string[] Accounts = [Domain.Permission.AccountsView];

    private static readonly string[] IncomeExpense = [Domain.Permission.IncomeExpenseView];

    public static readonly ReportLink DayBook =
        new("Transactions", "DayBook", "reports/day-book", Icons.Material.Filled.Today, Domain.Permission.ReportsTransactions, []);

    public static readonly ReportLink AllEntries =
        new("Transactions", "AllEntries", "reports/all-entries", Icons.Material.Filled.ListAlt, Domain.Permission.ReportsTransactions, []);

    public static readonly ReportLink PartyStatement =
        new("Parties", "PartyStatement", "reports/party-statement", Icons.Material.Filled.Person, Domain.Permission.ReportsParty, Parties);

    public static readonly ReportLink AllParties =
        new("Parties", "AllParties", "reports/all-parties", Icons.Material.Filled.People, Domain.Permission.ReportsParty, Parties);

    public static readonly ReportLink IncomeExpenseSummary =
        new("IncomeExpense", "IncomeExpense", "reports/income-expense", Icons.Material.Filled.PieChart, Domain.Permission.ReportsIncomeExpense, IncomeExpense);

    public static readonly ReportLink CashStatement =
        new("Business", "Cash", "reports/cash", Icons.Material.Filled.Payments, Domain.Permission.ReportsBusinessStatus, Accounts);

    public static readonly ReportLink BankStatement =
        new("Business", "Bank", "reports/bank", Icons.Material.Filled.AccountBalance, Domain.Permission.ReportsBusinessStatus, Accounts);

    public static readonly IReadOnlyList<ReportLink> All =
        [DayBook, AllEntries, PartyStatement, AllParties, IncomeExpenseSummary, CashStatement, BankStatement];
}
