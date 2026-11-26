namespace LekhaBook.Domain.Tests;

public sealed class TransactionViewRulesTests
{
    [Theory]
    [InlineData(TransactionType.PaymentIn, Permission.PaymentInView)]
    [InlineData(TransactionType.PaymentOut, Permission.PaymentOutView)]
    [InlineData(TransactionType.Expense, Permission.IncomeExpenseView)]
    [InlineData(TransactionType.OtherIncome, Permission.IncomeExpenseView)]
    [InlineData(TransactionType.TransferMoney, Permission.AccountsView)]
    [InlineData(TransactionType.PartyAdjustAdd, Permission.PartyAdjustmentsManage)]
    public void Viewing_an_entry_follows_its_type(TransactionType type, string expected) =>
        Assert.Equal(expected, EntryRules.ViewPermissionFor(type));

    [Fact]
    public void Every_type_has_a_view_permission_and_an_entry_person_holds_it()
    {
        var entryPerson = RoleTemplates.For(CompanyRole.EntryPerson);

        Assert.All(Enum.GetValues<TransactionType>(), type => Assert.Contains(EntryRules.ViewPermissionFor(type), entryPerson));
    }
}
