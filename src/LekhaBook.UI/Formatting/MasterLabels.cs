using MudBlazor;

namespace LekhaBook.UI.Formatting;

/// <summary>Words, colours and icons for parties and money accounts.</summary>
public static class MasterLabels
{
    /// <summary>A party's signed balance as "To Receive" / "To Give" / "Settled".</summary>
    public static (string Label, Color Color) PartySide(decimal balance) => balance switch
    {
        > 0 => ("To Receive", Color.Success),
        < 0 => ("To Give", Color.Error),
        _ => ("Settled", Color.Default),
    };

    /// <summary>The ledger header word: Receivable / Payable.</summary>
    public static string PartyBalanceTitle(decimal balance) => balance switch
    {
        > 0 => "Receivable",
        < 0 => "Payable",
        _ => "Settled",
    };

    public static string AccountType(string type) => type switch
    {
        "Bank" => "Bank account",
        "Wallet" => "Wallet",
        "Cheque" => "Cheque",
        _ => "Cash",
    };

    public static string AccountIcon(string type) => type switch
    {
        "Bank" => Icons.Material.Filled.AccountBalance,
        "Wallet" => Icons.Material.Filled.PhoneAndroid,
        "Cheque" => Icons.Material.Filled.Payments,
        _ => Icons.Material.Filled.Wallet,
    };

    /// <summary>Only the last 4 digits of an account number: "•••• 6789".</summary>
    public static string? MaskNumber(string? number) =>
        string.IsNullOrWhiteSpace(number) ? null
        : number.Length <= 4 ? number
        : "•••• " + number[^4..];

    /// <summary>Up to two initials for an avatar.</summary>
    public static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => (parts[0][..1] + parts[^1][..1]).ToUpperInvariant(),
        };
    }

    /// <summary>Transaction type keys (TransactionType) as users read them.</summary>
    public static string TransactionType(string type) => type switch
    {
        "PaymentIn" => "Payment In",
        "PaymentOut" => "Payment Out",
        "PartyAdjustAdd" => "Balance added",
        "PartyAdjustReduce" => "Balance reduced",
        "OtherIncome" => "Other Income",
        "AddMoney" => "Add Money",
        "ReduceMoney" => "Reduce Money",
        "TransferMoney" => "Transfer Money",
        _ => type,
    };
}
