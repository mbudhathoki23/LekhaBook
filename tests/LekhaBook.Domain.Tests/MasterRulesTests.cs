namespace LekhaBook.Domain.Tests;

public sealed class MasterRulesTests
{
    private static readonly DateOnly Today = new(2026, 11, 24);

    private static CompanyMember Member(CompanyRole role, IEnumerable<string>? custom = null) => new()
    {
        CompanyId = Guid.CreateVersion7(),
        UserId = Guid.CreateVersion7(),
        DisplayName = role.ToString(),
        Role = role,
        CustomPermissions = custom?.ToList(),
    };

    [Fact]
    public void Entry_person_adds_parties_but_only_dates_an_opening_balance_today()
    {
        var clerk = Member(CompanyRole.EntryPerson);

        Assert.Null(MasterRules.CheckCreateParty(clerk, PartyType.Customer, 500, Today, Today));
        Assert.Null(MasterRules.CheckCreateParty(clerk, PartyType.Supplier, 0, Today.AddDays(-10), Today)); // nothing to date
        Assert.NotNull(MasterRules.CheckCreateParty(clerk, PartyType.Customer, 500, Today.AddDays(-1), Today));
        Assert.Null(MasterRules.CheckCreateParty(Member(CompanyRole.Owner), PartyType.Customer, 500, Today.AddDays(-1), Today));
    }

    [Fact]
    public void Entry_person_cannot_edit_or_delete_parties()
    {
        var clerk = Member(CompanyRole.EntryPerson);

        Assert.NotNull(MasterRules.CheckEditParty(clerk, PartyType.Customer, PartyType.Customer, false, 0, Today, Today));
        Assert.NotNull(MasterRules.CheckDeleteParty(clerk, PartyType.Customer));
    }

    [Fact]
    public void Changing_the_party_type_needs_create_on_the_new_type()
    {
        var customersOnly = Member(CompanyRole.Accountant,
            RoleTemplates.For(CompanyRole.Accountant).Where(p => !p.StartsWith("parties.suppliers.", StringComparison.Ordinal)));

        Assert.Null(MasterRules.CheckEditParty(customersOnly, PartyType.Customer, PartyType.Customer, false, 0, Today, Today));
        Assert.NotNull(MasterRules.CheckEditParty(customersOnly, PartyType.Customer, PartyType.Supplier, false, 0, Today, Today));
        Assert.Null(MasterRules.CheckEditParty(Member(CompanyRole.Manager), PartyType.Customer, PartyType.Supplier, false, 0, Today, Today));
    }

    [Fact]
    public void Opening_date_rule_applies_to_edits_only_when_the_opening_changes()
    {
        // A custom Accountant who can edit customers but has Lock Transaction Date on.
        var locked = Member(CompanyRole.Accountant, [.. RoleTemplates.For(CompanyRole.Accountant), Permission.RestrictLockDate]);

        Assert.Null(MasterRules.CheckEditParty(locked, PartyType.Customer, PartyType.Customer, openingChanged: false, 500, Today.AddDays(-5), Today));
        Assert.NotNull(MasterRules.CheckEditParty(locked, PartyType.Customer, PartyType.Customer, openingChanged: true, 500, Today.AddDays(-5), Today));
    }

    [Fact]
    public void Built_in_and_used_money_accounts_cannot_be_deleted()
    {
        var cash = new Account { Name = "Cash", Type = AccountType.Cash, IsSystem = true };
        var bank = new Account { Name = "Bank", Type = AccountType.Bank };

        Assert.NotNull(MasterRules.CheckDeleteMoneyAccount(cash, usedByTransactions: false));
        Assert.NotNull(MasterRules.CheckDeleteMoneyAccount(bank, usedByTransactions: true));
        Assert.Null(MasterRules.CheckDeleteMoneyAccount(bank, usedByTransactions: false));
    }

    [Theory]
    [InlineData(PartyType.Customer, EntryAction.Create, Permission.CustomersCreate)]
    [InlineData(PartyType.Customer, EntryAction.Delete, Permission.CustomersDelete)]
    [InlineData(PartyType.Supplier, EntryAction.Edit, Permission.SuppliersEdit)]
    public void Party_permissions_follow_the_type(PartyType type, EntryAction action, string expected) =>
        Assert.Equal(expected, MasterRules.PartyPermission(type, action));
}
