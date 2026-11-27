namespace LekhaBook.Contracts;

/// <summary>
/// Add or reduce a balance without a payment: a party's due, or the money in a cash, bank, cheque or wallet account.
/// The client makes the <paramref name="Id"/> so a retried request can't post it twice; a null
/// <paramref name="Date"/> means today in the company's time zone.
/// </summary>
public sealed record AdjustBalanceRequest(bool Increase, decimal Amount, Guid Id, DateOnly? Date = null, string? Remarks = null);

/// <summary>A new expense or income head. <paramref name="Type"/> is "Expense" or "Income".</summary>
public sealed record SaveCategoryRequest(string Name, string Type, Guid Id);
