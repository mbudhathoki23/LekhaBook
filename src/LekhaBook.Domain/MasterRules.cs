namespace LekhaBook.Domain;

/// <summary>
/// Who may add, change or remove parties and money accounts (AGENT_PLAN § 4.2, D14). Pure rules: the server
/// runs them on every write; the UI uses <see cref="PartyPermission"/> only to hide buttons.
/// </summary>
public static class MasterRules
{
    /// <summary>The grant for <paramref name="action"/> on a party of <paramref name="type"/>; view uses <see cref="PartyViewPermission"/>.</summary>
    public static string PartyPermission(PartyType type, EntryAction action) => (type, action) switch
    {
        (PartyType.Customer, EntryAction.Create) => Permission.CustomersCreate,
        (PartyType.Customer, EntryAction.Edit) => Permission.CustomersEdit,
        (PartyType.Customer, EntryAction.Delete) => Permission.CustomersDelete,
        (PartyType.Supplier, EntryAction.Create) => Permission.SuppliersCreate,
        (PartyType.Supplier, EntryAction.Edit) => Permission.SuppliersEdit,
        (PartyType.Supplier, EntryAction.Delete) => Permission.SuppliersDelete,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static string PartyViewPermission(PartyType type) =>
        type == PartyType.Customer ? Permission.CustomersView : Permission.SuppliersView;

    /// <summary>Checks adding a party. <paramref name="companyToday"/> comes from <see cref="CompanyClock"/>.</summary>
    public static string? CheckCreateParty(CompanyMember member, PartyType type, decimal opening, DateOnly? openingAsOf, DateOnly companyToday)
    {
        if (!member.Has(PartyPermission(type, EntryAction.Create)))
        {
            return $"You don't have permission to add {Plural(type)}.";
        }

        return CheckOpeningDate(member, opening, openingAsOf, companyToday);
    }

    /// <summary>
    /// Checks editing a party. Changing its type needs edit on the old type and create on the new one.
    /// The opening date rule only applies when the opening balance itself changes.
    /// </summary>
    public static string? CheckEditParty(
        CompanyMember member, PartyType oldType, PartyType newType, bool openingChanged, decimal opening, DateOnly? openingAsOf, DateOnly companyToday)
    {
        if (!member.Has(PartyPermission(oldType, EntryAction.Edit)))
        {
            return $"You don't have permission to edit {Plural(oldType)}.";
        }

        if (newType != oldType && !member.Has(PartyPermission(newType, EntryAction.Create)))
        {
            return $"You don't have permission to add {Plural(newType)}, so you can't change this party to one.";
        }

        return openingChanged ? CheckOpeningDate(member, opening, openingAsOf, companyToday) : null;
    }

    /// <summary>Permission to delete the party; whether it's still in use is checked separately (a conflict, not a permission).</summary>
    public static string? CheckDeleteParty(CompanyMember member, PartyType type) =>
        member.Has(PartyPermission(type, EntryAction.Delete)) ? null : $"You don't have permission to delete {Plural(type)}.";

    public const string PartyInUse = "This party has transactions. Delete those first.";

    /// <summary>Checks adding or editing a money account (the endpoint policy already checked add/edit permission).</summary>
    public static string? CheckMoneyAccountOpening(CompanyMember member, decimal opening, DateOnly? openingAsOf, DateOnly companyToday) =>
        CheckOpeningDate(member, opening, openingAsOf, companyToday);

    public static string? CheckDeleteMoneyAccount(Account account, bool usedByTransactions)
    {
        if (account.IsSystem)
        {
            return $"{account.Name} is a built-in account and can't be deleted.";
        }

        return usedByTransactions
            ? "This account has transactions. Delete those first."
            : null;
    }

    /// <summary>
    /// Lock Transaction Date also covers opening balances: a restricted user can only date one today.
    /// No opening balance means nothing to date.
    /// </summary>
    private static string? CheckOpeningDate(CompanyMember member, decimal opening, DateOnly? openingAsOf, DateOnly companyToday) =>
        opening != 0 && member.Has(Permission.RestrictLockDate) && openingAsOf is { } d && d != companyToday
            ? "You can only date an opening balance today."
            : null;

    // ---------------- Transactions ----------------

    /// <summary>Checks adding a transaction of <paramref name="type"/>. <paramref name="companyToday"/> comes from CompanyClock.</summary>
    public static string? CheckTransactionCreate(CompanyMember member, TransactionType type, decimal amount, DateOnly date, DateOnly companyToday)
    {
        if (!member.Has(TransactionPermission(type, EntryAction.Create)))
        {
            return $"You don't have permission to create {TransactionTypeName(type)}.";
        }

        if (amount <= 0)
        {
            return "Amount must be greater than zero.";
        }

        if (date > companyToday)
        {
            return "Date cannot be in the future.";
        }

        return null;
    }

    public static string? CheckTransactionEdit(CompanyMember member, TransactionType type, DateOnly date, decimal amount, bool sameDayOnly, DateOnly companyToday)
    {
        if (!member.Has(TransactionPermission(type, EntryAction.Edit)))
        {
            return $"You don't have permission to edit {TransactionTypeName(type)}.";
        }

        if (amount <= 0)
        {
            return "Amount must be greater than zero.";
        }

        if (date > companyToday)
        {
            return "Date cannot be in the future.";
        }

        if (sameDayOnly && date != companyToday)
        {
            return "You can only edit transactions dated today.";
        }

        return null;
    }

    public static string? CheckTransactionDelete(CompanyMember member, TransactionType type, DateOnly date, DateOnly companyToday)
    {
        if (!member.Has(TransactionPermission(type, EntryAction.Delete)))
        {
            return $"You don't have permission to delete {TransactionTypeName(type)}.";
        }

        if (date > companyToday)
        {
            return "Date cannot be in the future.";
        }

        if (date != companyToday)
        {
            return "You can only delete transactions dated today.";
        }

        return null;
    }

    /// <summary>Checks editing a transaction. Same-day-only rule applies when requested.</summary>
    public static string? CheckEditTransaction(CompanyMember member, TransactionType type, DateOnly date, decimal amount, bool sameDayOnly, DateOnly companyToday)
    {
        if (!member.Has(TransactionPermission(type, EntryAction.Edit)))
        {
            return $"You don't have permission to edit {TransactionTypeName(type)}.";
        }

        if (amount <= 0)
        {
            return "Amount must be greater than zero.";
        }

        if (date > companyToday)
        {
            return "Date cannot be in the future.";
        }

        if (sameDayOnly && date != companyToday)
        {
            return "You can only edit transactions dated today.";
        }

        return null;
    }

    /// <summary>Permission to create, edit or delete a transaction of <paramref name="type"/>.</summary>
    public static string TransactionPermission(TransactionType type, EntryAction action) => (type, action) switch
    {
        (TransactionType.PaymentIn, EntryAction.Create) => Permission.PaymentInCreate,
        (TransactionType.PaymentIn, EntryAction.Edit) => Permission.PaymentInEdit,
        (TransactionType.PaymentIn, EntryAction.Delete) => Permission.PaymentInDelete,
        (TransactionType.PaymentOut, EntryAction.Create) => Permission.PaymentOutCreate,
        (TransactionType.PaymentOut, EntryAction.Edit) => Permission.PaymentOutEdit,
        (TransactionType.PaymentOut, EntryAction.Delete) => Permission.PaymentOutDelete,
        (TransactionType.PartyAdjustAdd, EntryAction.Create) => Permission.PartyAdjustmentsManage,
        (TransactionType.PartyAdjustAdd, EntryAction.Edit) => Permission.PartyAdjustmentsManage,
        (TransactionType.PartyAdjustAdd, EntryAction.Delete) => Permission.PartyAdjustmentsManage,
        (TransactionType.PartyAdjustReduce, EntryAction.Create) => Permission.PartyAdjustmentsManage,
        (TransactionType.PartyAdjustReduce, EntryAction.Edit) => Permission.PartyAdjustmentsManage,
        (TransactionType.PartyAdjustReduce, EntryAction.Delete) => Permission.PartyAdjustmentsManage,
        (TransactionType.Expense, EntryAction.Create) => Permission.IncomeExpenseCreate,
        (TransactionType.Expense, EntryAction.Edit) => Permission.IncomeExpenseEdit,
        (TransactionType.Expense, EntryAction.Delete) => Permission.IncomeExpenseDelete,
        (TransactionType.OtherIncome, EntryAction.Create) => Permission.IncomeExpenseCreate,
        (TransactionType.OtherIncome, EntryAction.Edit) => Permission.IncomeExpenseEdit,
        (TransactionType.OtherIncome, EntryAction.Delete) => Permission.IncomeExpenseDelete,
        (TransactionType.AddMoney, EntryAction.Create) => Permission.AccountsAdjust,
        (TransactionType.AddMoney, EntryAction.Edit) => Permission.AccountsAdjustEdit,
        (TransactionType.AddMoney, EntryAction.Delete) => Permission.AccountsAdjustEdit,
        (TransactionType.ReduceMoney, EntryAction.Create) => Permission.AccountsAdjust,
        (TransactionType.ReduceMoney, EntryAction.Edit) => Permission.AccountsAdjustEdit,
        (TransactionType.ReduceMoney, EntryAction.Delete) => Permission.AccountsAdjustEdit,
        (TransactionType.TransferMoney, EntryAction.Create) => Permission.AccountsAdjust,
        (TransactionType.TransferMoney, EntryAction.Edit) => Permission.AccountsAdjustEdit,
        (TransactionType.TransferMoney, EntryAction.Delete) => Permission.AccountsAdjustEdit,
        _ => throw new ArgumentOutOfRangeException(nameof(type), $"Unknown transaction type: {type}")
    };

    /// <summary>Convenience method for creating permission checks.</summary>
    public static string TransactionCreatePermission(TransactionType type) => TransactionPermission(type, EntryAction.Create);
    public static string TransactionEditPermission(TransactionType type) => TransactionPermission(type, EntryAction.Edit);
    public static string TransactionDeletePermission(TransactionType type) => TransactionPermission(type, EntryAction.Delete);

    public static string TransactionTypeName(TransactionType type) => type switch
    {
        TransactionType.PaymentIn => "Payment In",
        TransactionType.PaymentOut => "Payment Out",
        TransactionType.PartyAdjustAdd => "Party Adjust Add",
        TransactionType.PartyAdjustReduce => "Party Adjust Reduce",
        TransactionType.Expense => "Expense",
        TransactionType.OtherIncome => "Other Income",
        TransactionType.AddMoney => "Add Money",
        TransactionType.ReduceMoney => "Reduce Money",
        TransactionType.TransferMoney => "Transfer Money",
        _ => "Unknown",
    };

    private static string Plural(PartyType type) => type == PartyType.Customer ? "customers" : "suppliers";
}