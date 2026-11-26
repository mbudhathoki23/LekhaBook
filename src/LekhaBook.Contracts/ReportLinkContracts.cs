namespace LekhaBook.Contracts;

// Public report links: a read-only statement that can be sent by WhatsApp or email (decision D24).

public sealed record CreateReportLinkRequest(Guid AccountId, int ExpiresInDays = 30, bool IncludeFiles = true);

/// <summary>
/// A link that still works. Its address can't be shown again, because only a hash of the token is kept. The dates are
/// the company's own, so they match the public page wherever the web server runs.
/// </summary>
public sealed record ReportLinkDto(Guid Id, Guid AccountId, bool IncludeFiles, DateOnly CreatedOn, DateOnly ExpiresOn);

/// <summary>A new link with its address, returned once.</summary>
public sealed record CreatedReportLinkDto(ReportLinkDto Link, string ReportUrl, string SignInUrl);

/// <summary>What anyone holding the link sees.</summary>
public sealed record PublicReportDto(
    string CompanyName,
    string? CompanyAddress,
    string? CompanyPhone,
    string? CompanyPan,
    bool HasLogo,
    string Calendar,
    string Name,
    string? PartyType,
    string? AccountType,
    string? Address,
    string? Pan,
    string? AccountHolderName,
    string? AccountNumber,
    DateOnly GeneratedOn,
    DateOnly ExpiresOn,
    decimal OpeningBalance,
    DateOnly? OpeningAsOf,
    decimal ClosingBalance,
    IReadOnlyList<PublicReportLine> Lines);

/// <summary>One statement line. <paramref name="VoucherNo"/> is empty while the number is provisional.</summary>
public sealed record PublicReportLine(
    string Type,
    string VoucherNo,
    DateOnly Date,
    decimal Debit,
    decimal Credit,
    decimal Balance,
    string? OtherAccount,
    string? Remarks,
    string? ChequeNo,
    IReadOnlyList<PublicReportFile> Files);

public sealed record PublicReportFile(Guid Id, string FileName, string ContentType);

public sealed record ResolveReportLinkRequest(string Token);

/// <summary>Where a signed-in member finds the shared statement inside the app.</summary>
public sealed record ResolvedReportLinkDto(Guid CompanyId, Guid AccountId, bool IsParty);

/// <summary>The header that carries a link's token to the API, so tokens stay out of request logs.</summary>
public static class ReportLinkHeaders
{
    public const string Token = "X-Report-Token";
}
