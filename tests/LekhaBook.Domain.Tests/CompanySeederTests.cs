using LekhaBook.Domain;

namespace LekhaBook.Domain.Tests;

public sealed class CompanySeederTests
{
    [Fact]
    public void New_company_gets_money_adjustment_and_category_accounts()
    {
        var companyId = Guid.CreateVersion7();

        var accounts = CompanySeeder.DefaultAccounts(companyId, TestBooks.Now);

        Assert.All(accounts, a => Assert.Equal(companyId, a.CompanyId));
        Assert.Single(accounts, a => a.Type == AccountType.Cash && a.IsSystem);
        Assert.Single(accounts, a => a.Type == AccountType.Cheque);
        Assert.Single(accounts, a => a.Type == AccountType.Wallet);
        Assert.Single(accounts, a => a.Type == AccountType.System && a.Name == CompanySeeder.AdjustmentAccountName && a.IsSystem);
        Assert.Equal(CompanySeeder.ExpenseHeads, accounts.Where(a => a.Type == AccountType.Expense).Select(a => a.Name));
        Assert.Equal(CompanySeeder.IncomeHeads, accounts.Where(a => a.Type == AccountType.Income).Select(a => a.Name));
        Assert.DoesNotContain(accounts, a => a.Type is AccountType.Bank or AccountType.Party);
        Assert.Equal(accounts.Count, accounts.Select(a => a.Id).Distinct().Count());
        Assert.All(accounts, a => Assert.Equal(0m, a.OpeningBalance));
    }
}
