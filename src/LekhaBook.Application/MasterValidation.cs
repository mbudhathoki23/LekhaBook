using System.Net.Mail;
using LekhaBook.Contracts;
using LekhaBook.Domain;

namespace LekhaBook.Application;

/// <summary>
/// Field checks for party and money-account forms. The same rules run in the form (instant feedback)
/// and on the server (the one that counts). Keys are the request's camelCase property names.
/// </summary>
public static class MasterValidation
{
    public const int NameMax = 200;
    public const int PhoneMax = 30;
    public const int EmailMax = 256;
    public const int AddressMax = 500;
    public const int PanMax = 15;
    public const int AccountNumberMax = 50;
    public const int RemarksMax = 500;
    public const int ChequeNoMax = 50;
    public const int VoucherNoMax = 50;

    /// <summary>Account types a user can add on the Cash &amp; Bank screen (Cheque is built in).</summary>
    public static readonly AccountType[] AddableMoneyTypes = [AccountType.Bank, AccountType.Wallet, AccountType.Cash];

    public static Dictionary<string, string[]> Validate(SavePartyRequest r)
    {
        var errors = new Dictionary<string, string[]>();
        Required(errors, "name", r.Name, NameMax, "Full name");
        if (ParsePartyType(r.PartyType) is null)
        {
            errors["partyType"] = ["Choose Customer or Supplier."];
        }

        Phone(errors, r.Phone);
        if (!string.IsNullOrWhiteSpace(r.Email) && (r.Email.Trim().Length > EmailMax || !MailAddress.TryCreate(r.Email.Trim(), out _)))
        {
            errors["email"] = ["Enter a valid email address."];
        }

        Optional(errors, "address", r.Address, AddressMax, "Address");
        if (!string.IsNullOrWhiteSpace(r.Pan) && (r.Pan.Trim().Length > PanMax || !r.Pan.Trim().All(char.IsAsciiDigit)))
        {
            errors["pan"] = [$"PAN must be digits only, at most {PanMax}."];
        }

        if (r.OpeningBalance != 0 && (r.OpeningBalance < 0 || !PostingRules.IsValidAmount(r.OpeningBalance)))
        {
            errors["openingBalance"] = ["Opening balance must be a positive amount with at most 2 decimals. Use To Give for money you owe."];
        }

        if (ParseOpeningSide(r.OpeningSide) is null)
        {
            errors["openingSide"] = ["Choose To Receive or To Give."];
        }

        return errors;
    }

    public static Dictionary<string, string[]> Validate(SaveMoneyAccountRequest r, bool isNew)
    {
        var errors = new Dictionary<string, string[]>();
        Required(errors, "name", r.Name, NameMax, "Name");
        if (ParseMoneyType(r.Type) is not { } type || (isNew && !AddableMoneyTypes.Contains(type)))
        {
            errors["type"] = ["Choose Bank, Wallet or Cash."];
        }

        Optional(errors, "accountHolderName", r.AccountHolderName, NameMax, "Account holder name");
        Optional(errors, "accountNumber", r.AccountNumber, AccountNumberMax, "Account number");
        if (r.OpeningBalance != 0 && !PostingRules.IsValidAmount(Math.Abs(r.OpeningBalance)))
        {
            errors["openingBalance"] = ["Enter an amount with at most 2 decimals."];
        }

        return errors;
    }

    public static Dictionary<string, string[]> Validate(SavePartyCategoryRequest r)
    {
        var errors = new Dictionary<string, string[]>();
        Required(errors, "name", r.Name, NameMax, "Category name");
        return errors;
    }

    public static Dictionary<string, string[]> Validate(CreateTransactionRequest r)
    {
        var errors = new Dictionary<string, string[]>();
        Required(errors, "voucherNo", r.Id?.ToString() ?? string.Empty, VoucherNoMax, "Voucher no");
        Required(errors, "type", r.Type, 50, "Type");
        if (!PostingRules.IsValidAmount(r.Amount))
        {
            errors["amount"] = ["Amount must be a positive number with at most 2 decimals."];
        }


        if (r.FromAccountId == Guid.Empty)
        {
            errors["fromAccountId"] = ["From account is required."];
        }

        if (r.ToAccountId == Guid.Empty)
        {
            errors["toAccountId"] = ["To account is required."];
        }

        Optional(errors, "remarks", r.Remarks, RemarksMax, "Remarks");

        return errors;
    }

    public static PartyType? ParsePartyType(string? value) =>
        Enum.TryParse<PartyType>(value, ignoreCase: true, out var t) && Enum.IsDefined(t) ? t : null;

    /// <summary>"ToReceive" → Debit, "ToGive" → Credit.</summary>
    public static BalanceSide? ParseOpeningSide(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "toreceive" => BalanceSide.Debit,
        "togive" => BalanceSide.Credit,
        _ => null,
    };

    public static string OpeningSideName(BalanceSide side) => side == BalanceSide.Debit ? "ToReceive" : "ToGive";

    /// <summary>A money account type (Cash, Bank, Cheque, Wallet), or null.</summary>
    public static AccountType? ParseMoneyType(string? value) =>
        Enum.TryParse<AccountType>(value, ignoreCase: true, out var t) && Enum.IsDefined(t) && t.IsMoney() ? t : null;

    /// <summary>Trims, and turns blank into null.</summary>
    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void Required(Dictionary<string, string[]> errors, string key, string? value, int max, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[key] = [$"{label} is required."];
        }
        else if (value.Trim().Length > max)
        {
            errors[key] = [$"{label} can be at most {max} characters."];
        }
    }

    private static void Optional(Dictionary<string, string[]> errors, string key, string? value, int max, string label)
    {
        if (value is not null && value.Trim().Length > max)
        {
            errors[key] = [$"{label} can be at most {max} characters."];
        }
    }

    private static void Phone(Dictionary<string, string[]> errors, string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return;
        }

        var p = phone.Trim();
        if (p.Length > PhoneMax || !p.All(c => char.IsAsciiDigit(c) || c is '+' or '-' or ' ' or '(' or ')'))
        {
            errors["phone"] = ["Phone can contain digits, spaces, +, - and brackets."];
        }
    }
}
