namespace LekhaBook.Domain;

public static class AccountTypeExtensions
{
    /// <summary>Cash, Bank, Cheque and Wallet: what forms call "Payment Method".</summary>
    public static bool IsMoney(this AccountType type) =>
        type is AccountType.Cash or AccountType.Bank or AccountType.Cheque or AccountType.Wallet;
}
