using MudBlazor;

namespace LekhaBook.UI.Formatting;

/// <summary>
/// Words, colours and icons for parties and money accounts. Words are resource keys: show them with
/// <c>@Loc[MasterLabels.…]</c>.
/// </summary>
public static class MasterLabels
{
    /// <summary>A party's signed balance as a resource key ("PartySide.ToReceive" / "PartySide.ToGive" / "PartySide.Settled") and colour.</summary>
    public static (string LabelKey, Color Color) PartySide(decimal balance) => balance switch
    {
        > 0 => ("PartySide.ToReceive", Color.Success),
        < 0 => ("PartySide.ToGive", Color.Error),
        _ => ("PartySide.Settled", Color.Default),
    };

    /// <summary>The ledger header word as a resource key: "PartyBalance.Receivable" / "PartyBalance.Payable" / "PartySide.Settled".</summary>
    public static string PartyBalanceTitle(decimal balance) => balance switch
    {
        > 0 => "PartyBalance.Receivable",
        < 0 => "PartyBalance.Payable",
        _ => "PartySide.Settled",
    };

    /// <summary>The resource key for a money account type ("Bank", "Wallet", "Cheque", "Cash").</summary>
    public static string AccountType(string type) => type switch
    {
        "Bank" => "AccountType.Bank",
        "Wallet" => "AccountType.Wallet",
        "Cheque" => "AccountType.Cheque",
        _ => "AccountType.Cash",
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

    /// <summary>The resource key for a transaction type (TransactionType). Unknown types are returned unchanged.</summary>
    public static string TransactionType(string type) => type switch
    {
        "PaymentIn" => "TxnType.PaymentIn",
        "PaymentOut" => "TxnType.PaymentOut",
        "PartyAdjustAdd" => "TxnType.PartyAdjustAdd",
        "PartyAdjustReduce" => "TxnType.PartyAdjustReduce",
        "Expense" => "Transactions.Type.Expense",
        "OtherIncome" => "TxnType.OtherIncome",
        "AddMoney" => "TxnType.AddMoney",
        "ReduceMoney" => "TxnType.ReduceMoney",
        "TransferMoney" => "TxnType.TransferMoney",
        _ => type,
    };
}
