namespace LekhaBook.Domain;

/// <summary>
/// A public, read-only link to one party or money account statement. Server-only: devices never sync it.
/// Only a hash of the token is kept, and the statement is computed on every view, so no balance is stored.
/// </summary>
public sealed class ReportShareLink
{
    public const int DefaultDays = 30;
    public const int MaxDays = 90;

    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid CompanyId { get; set; }

    /// <summary>The party or money account whose statement the link shows.</summary>
    public Guid AccountId { get; set; }

    /// <summary>SHA-256 of the token, in hex. The token itself is shown once, when the link is made.</summary>
    public required string TokenHash { get; set; }

    /// <summary>Whether viewers also see the bill and voucher files attached to the entries.</summary>
    public bool IncludeFiles { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    public DateTime? RevokedAtUtc { get; set; }

    public bool IsLive(DateTime nowUtc) => RevokedAtUtc is null && nowUtc < ExpiresAtUtc;

    /// <summary>Whether <paramref name="account"/> is something a statement can be shared for.</summary>
    public static bool CanShare(Account account) => account.Type == AccountType.Party || account.IsMoneyAccount;

    /// <summary>
    /// Whether the member may publish this statement: export rights plus the right to see it. Checked when the link is
    /// made and again on every view, so a link stops working when its creator loses access.
    /// </summary>
    public static bool MayShare(CompanyMember member, Account account) =>
        member.IsActive
        && CanShare(account)
        && member.Has(Permission.ReportsExport)
        && member.Has(ViewPermission(account));

    /// <summary>The permission needed to see the account's statement.</summary>
    public static string ViewPermission(Account account) =>
        account.Type == AccountType.Party
            ? MasterRules.PartyViewPermission(account.PartyType ?? PartyType.Customer)
            : Permission.AccountsView;
}

/// <summary>How bank and wallet numbers are shown wherever the full number isn't needed.</summary>
public static class AccountNumbers
{
    /// <summary>Only the last 4 digits: "•••• 6789".</summary>
    public static string? Mask(string? number) =>
        string.IsNullOrWhiteSpace(number) ? null
        : number.Length <= 4 ? number
        : "•••• " + number[^4..];
}
