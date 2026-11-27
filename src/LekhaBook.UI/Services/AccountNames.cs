namespace LekhaBook.UI.Services;

/// <summary>Names for the account ids on entries: parties, money accounts and heads the user may view.</summary>
public static class AccountNames
{
    public static async Task<Dictionary<Guid, string>> LoadAsync(IDataService data, ISessionService session, Guid companyId)
    {
        var names = new Dictionary<Guid, string>();
        if (session.Can(Domain.Permission.CustomersView) || session.Can(Domain.Permission.SuppliersView))
        {
            foreach (var party in (await data.GetPartiesAsync(companyId)).Value ?? [])
            {
                names[party.Id] = party.Name;
            }
        }

        if (session.Can(Domain.Permission.AccountsView))
        {
            foreach (var account in (await data.GetMoneyAccountsAsync(companyId)).Value ?? [])
            {
                names[account.Id] = account.Name;
            }
        }

        if (session.Can(Domain.Permission.IncomeExpenseView))
        {
            foreach (var category in (await data.GetCategoriesAsync(companyId)).Value ?? [])
            {
                names[category.Id] = category.Name;
            }
        }

        return names;
    }
}
