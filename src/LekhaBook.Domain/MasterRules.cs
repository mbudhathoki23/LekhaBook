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

    public const string PartyInUse = "This party has entries. Delete those first.";

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
            ? "This account has entries. Delete those first."
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

    private static string Plural(PartyType type) => type == PartyType.Customer ? "customers" : "suppliers";
}