namespace LekhaBook.Domain;

/// <summary>
/// Permission keys (AGENT_PLAN § 4.2). Stored as stable strings, so they can be renamed in code
/// without migrating data. Keys under <c>restrict.</c> are restrictions: holding one limits the user.
/// </summary>
public static class Permission
{
    public const string CustomersCreate = "parties.customers.create";
    public const string CustomersView = "parties.customers.view";
    public const string CustomersEdit = "parties.customers.edit";
    public const string CustomersDelete = "parties.customers.delete";

    public const string SuppliersCreate = "parties.suppliers.create";
    public const string SuppliersView = "parties.suppliers.view";
    public const string SuppliersEdit = "parties.suppliers.edit";
    public const string SuppliersDelete = "parties.suppliers.delete";

    public const string PaymentInCreate = "payments_in.create";
    public const string PaymentInView = "payments_in.view";
    public const string PaymentInEdit = "payments_in.edit";
    public const string PaymentInDelete = "payments_in.delete";

    public const string PaymentOutCreate = "payments_out.create";
    public const string PaymentOutView = "payments_out.view";
    public const string PaymentOutEdit = "payments_out.edit";
    public const string PaymentOutDelete = "payments_out.delete";

    public const string IncomeExpenseCreate = "income_expense.create";
    public const string IncomeExpenseView = "income_expense.view";
    public const string IncomeExpenseEdit = "income_expense.edit";
    public const string IncomeExpenseDelete = "income_expense.delete";

    public const string AccountsView = "accounts.view";
    public const string AccountsAdd = "accounts.add";
    public const string AccountsEdit = "accounts.edit";
    public const string AccountsDelete = "accounts.delete";

    /// <summary>Add Money, Reduce Money, Transfer Money.</summary>
    public const string AccountsAdjust = "accounts.adjust";

    /// <summary>Edit or delete an Add/Reduce/Transfer Money entry.</summary>
    public const string AccountsAdjustEdit = "accounts.adjust_edit";

    /// <summary>Party Adjust Balance (add/reduce).</summary>
    public const string PartyAdjustmentsManage = "party_adjustments.manage";

    /// <summary>Transactions (all types: PaymentIn, PaymentOut, Expense, Income, AddMoney, ReduceMoney, TransferMoney).</summary>
    public const string TransactionsView = "transactions.view";
    public const string TransactionsEdit = "transactions.edit";
    public const string TransactionsDelete = "transactions.delete";

    /// <summary>Edit or delete entries created by someone else.</summary>
    public const string EntriesEditOthers = "entries.edit_others";

    public const string ReportsParty = "reports.party";
    public const string ReportsTransactions = "reports.transactions";
    public const string ReportsIncomeExpense = "reports.income_expense";
    public const string ReportsBusinessStatus = "reports.business_status";
    public const string ReportsDashboard = "reports.dashboard";
    public const string ReportsExport = "reports.export";

    public const string StaffCreate = "staff.create";
    public const string StaffView = "staff.view";
    public const string StaffEdit = "staff.edit";
    public const string StaffDelete = "staff.delete";

    /// <summary>Edit the business profile.</summary>
    public const string SettingsBusiness = "settings.business";

    /// <summary>Party and transaction settings, including number prefixes and serial reset.</summary>
    public const string SettingsFeatures = "settings.features";

    public const string BusinessDelete = "business.delete";

    /// <summary>Restriction: entries can only be dated today (company time zone).</summary>
    public const string RestrictLockDate = "restrict.lock_date";

    /// <summary>Restriction: own entries can only be edited or deleted on the day they were created.</summary>
    public const string RestrictSameDayEdits = "restrict.same_day_edits";

    /// <summary>All grants (not restrictions).</summary>
    public static IReadOnlySet<string> AllGrants { get; } = new HashSet<string>(
        typeof(Permission).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(k => !IsRestriction(k)),
        StringComparer.Ordinal);

    public static IReadOnlySet<string> AllRestrictions { get; } =
        new HashSet<string>([RestrictLockDate, RestrictSameDayEdits], StringComparer.Ordinal);

    public static bool IsRestriction(string key) => key.StartsWith("restrict.", StringComparison.Ordinal);

    public static bool IsKnown(string key) => AllGrants.Contains(key) || AllRestrictions.Contains(key);
}

public enum CompanyRole
{
    Owner,
    Partner,
    Manager,
    Accountant,
    EntryPerson,
}

public enum MemberStatus
{
    Active,
    Revoked,
}

/// <summary>Default permissions for each role (AGENT_PLAN § 4.2). Owner is unrestricted and can't be customised.</summary>
public static class RoleTemplates
{
    public static IReadOnlySet<string> For(CompanyRole role) => role switch
    {
        CompanyRole.Owner => Permission.AllGrants,
        CompanyRole.Partner => Partner,
        CompanyRole.Manager => Manager,
        CompanyRole.Accountant => Accountant,
        CompanyRole.EntryPerson => EntryPerson,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private static readonly HashSet<string> Partner = Grants(except: [Permission.BusinessDelete]);

    private static readonly HashSet<string> Accountant = Grants(except:
    [
        Permission.StaffCreate, Permission.StaffView, Permission.StaffEdit, Permission.StaffDelete,
        Permission.SettingsBusiness, Permission.BusinessDelete, Permission.AccountsDelete,
    ]);

    // Create, view and edit everywhere; no deletes, staff, settings, or account add/edit.
    private static readonly HashSet<string> Manager = new(Permission.AllGrants
        .Where(k => !k.EndsWith(".delete", StringComparison.Ordinal))
        .Where(k => !k.StartsWith("staff.", StringComparison.Ordinal))
        .Where(k => !k.StartsWith("settings.", StringComparison.Ordinal))
        .Except([Permission.AccountsAdd, Permission.AccountsEdit, Permission.AccountsAdjustEdit, Permission.BusinessDelete]),
        StringComparer.Ordinal);

    // Fast data entry on a phone: create and view, edit/delete only their own entries the same day,
    // can only date entries today, and sees balances (dashboard + party) but can't export.
    private static readonly HashSet<string> EntryPerson = new(
    [
        Permission.CustomersCreate, Permission.CustomersView,
        Permission.SuppliersCreate, Permission.SuppliersView,
        Permission.PaymentInCreate, Permission.PaymentInView, Permission.PaymentInEdit, Permission.PaymentInDelete,
        Permission.PaymentOutCreate, Permission.PaymentOutView, Permission.PaymentOutEdit, Permission.PaymentOutDelete,
        Permission.IncomeExpenseCreate, Permission.IncomeExpenseView, Permission.IncomeExpenseEdit, Permission.IncomeExpenseDelete,
        Permission.AccountsView, Permission.AccountsAdjust, Permission.PartyAdjustmentsManage,
        Permission.ReportsDashboard, Permission.ReportsParty,
        Permission.RestrictLockDate, Permission.RestrictSameDayEdits,
    ], StringComparer.Ordinal);

    private static HashSet<string> Grants(string[] except) =>
        new(Permission.AllGrants.Except(except), StringComparer.Ordinal);
}
