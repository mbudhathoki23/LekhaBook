using System.Globalization;
using System.Net.Mail;
using LekhaBook.Contracts;
using LekhaBook.Domain;

namespace LekhaBook.Application;

/// <summary>Puts a validation message into words: a key of <see cref="MasterValidation.Messages"/> and its arguments.</summary>
public delegate string ValidationText(string key, params object[] args);

/// <summary>
/// Field checks for party, money-account and transaction forms. The same rules run in the form (instant feedback)
/// and on the server (the one that counts). Keys are the request's camelCase property names.
/// Messages are English unless the caller passes a <see cref="ValidationText"/>, as the forms do for the
/// screen's language.
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
    public const int ChequeNoMax = 40; // the database column

    /// <summary>The English wording of every message and field name. The UI's resource files use the same keys.</summary>
    public static readonly IReadOnlyDictionary<string, string> Messages = new Dictionary<string, string>
    {
        ["Validation.Required"] = "{0} is required.",
        ["Validation.TooLong"] = "{0} can be at most {1} characters.",
        ["Validation.PartyType"] = "Choose Customer or Supplier.",
        ["Validation.Email"] = "Enter a valid email address.",
        ["Validation.Pan"] = "PAN must be digits only, at most {0}.",
        ["Validation.PartyOpening"] = "Opening balance must be a positive amount with at most 2 decimals. Use To Give for money you owe.",
        ["Validation.OpeningSide"] = "Choose To Receive or To Give.",
        ["Validation.MoneyType"] = "Choose Bank, Wallet or Cash.",
        ["Validation.Decimals"] = "Enter an amount with at most 2 decimals.",
        ["Validation.Phone"] = "Phone can contain digits, spaces, +, - and brackets.",
        ["Validation.TransactionType"] = "Choose an entry type.",
        ["Validation.Amount"] = "Amount must be more than 0 with at most 2 decimals.",
        ["Validation.Account"] = "Choose an account.",
        ["Validation.Field.FullName"] = "Full name",
        ["Validation.Field.Name"] = "Name",
        ["Validation.Field.CategoryName"] = "Category name",
        ["Validation.Field.Address"] = "Address",
        ["Validation.Field.AccountHolderName"] = "Account holder name",
        ["Validation.Field.AccountNumber"] = "Account number",
        ["Validation.Field.Remarks"] = "Remarks",
        ["Validation.Field.ChequeNo"] = "Cheque no.",
    };

    /// <summary>Account types a user can add on the Finance screen (Cheque is built in).</summary>
    public static readonly AccountType[] AddableMoneyTypes = [AccountType.Bank, AccountType.Wallet, AccountType.Cash];

    public static Dictionary<string, string[]> Validate(SavePartyRequest r, ValidationText? text = null)
    {
        text ??= English;
        var errors = new Dictionary<string, string[]>();
        Required(errors, text, "name", r.Name, NameMax, "FullName");
        if (ParsePartyType(r.PartyType) is null)
        {
            errors["partyType"] = [text("Validation.PartyType")];
        }

        Phone(errors, text, r.Phone);
        if (!string.IsNullOrWhiteSpace(r.Email) && (r.Email.Trim().Length > EmailMax || !MailAddress.TryCreate(r.Email.Trim(), out _)))
        {
            errors["email"] = [text("Validation.Email")];
        }

        Optional(errors, text, "address", r.Address, AddressMax, "Address");
        if (!string.IsNullOrWhiteSpace(r.Pan) && (r.Pan.Trim().Length > PanMax || !r.Pan.Trim().All(char.IsAsciiDigit)))
        {
            errors["pan"] = [text("Validation.Pan", PanMax)];
        }

        if (r.OpeningBalance != 0 && (r.OpeningBalance < 0 || !PostingRules.IsValidAmount(r.OpeningBalance)))
        {
            errors["openingBalance"] = [text("Validation.PartyOpening")];
        }

        if (ParseOpeningSide(r.OpeningSide) is null)
        {
            errors["openingSide"] = [text("Validation.OpeningSide")];
        }

        return errors;
    }

    public static Dictionary<string, string[]> Validate(SaveMoneyAccountRequest r, bool isNew, ValidationText? text = null)
    {
        text ??= English;
        var errors = new Dictionary<string, string[]>();
        Required(errors, text, "name", r.Name, NameMax, "Name");
        if (ParseMoneyType(r.Type) is not { } type || (isNew && !AddableMoneyTypes.Contains(type)))
        {
            errors["type"] = [text("Validation.MoneyType")];
        }

        Optional(errors, text, "accountHolderName", r.AccountHolderName, NameMax, "AccountHolderName");
        Optional(errors, text, "accountNumber", r.AccountNumber, AccountNumberMax, "AccountNumber");
        if (r.OpeningBalance != 0 && !PostingRules.IsValidAmount(Math.Abs(r.OpeningBalance)))
        {
            errors["openingBalance"] = [text("Validation.Decimals")];
        }

        return errors;
    }

    public static Dictionary<string, string[]> Validate(SavePartyCategoryRequest r, ValidationText? text = null)
    {
        var errors = new Dictionary<string, string[]>();
        Required(errors, text ?? English, "name", r.Name, NameMax, "CategoryName");
        return errors;
    }

    public static Dictionary<string, string[]> Validate(CreateTransactionRequest r, ValidationText? text = null)
    {
        text ??= English;
        var errors = EntryFields(text, r.Amount, r.FromAccountId, r.ToAccountId, r.Remarks, r.ChequeNo);
        if (ParseTransactionType(r.Type) is null)
        {
            errors["type"] = [text("Validation.TransactionType")];
        }

        return errors;
    }

    public static Dictionary<string, string[]> Validate(UpdateTransactionRequest r, ValidationText? text = null) =>
        EntryFields(text ?? English, r.Amount, r.FromAccountId, r.ToAccountId, r.Remarks, r.ChequeNo);

    /// <summary>A TransactionType name, or null. Numbers aren't accepted.</summary>
    public static TransactionType? ParseTransactionType(string? value) =>
        Enum.GetValues<TransactionType>().Cast<TransactionType?>()
            .FirstOrDefault(t => string.Equals(t.ToString(), value, StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, string[]> EntryFields(
        ValidationText text, decimal amount, Guid from, Guid to, string? remarks, string? chequeNo)
    {
        var errors = new Dictionary<string, string[]>();
        if (!PostingRules.IsValidAmount(amount))
        {
            errors["amount"] = [text("Validation.Amount")];
        }

        if (from == Guid.Empty)
        {
            errors["fromAccountId"] = [text("Validation.Account")];
        }

        if (to == Guid.Empty)
        {
            errors["toAccountId"] = [text("Validation.Account")];
        }

        Optional(errors, text, "remarks", remarks, RemarksMax, "Remarks");
        Optional(errors, text, "chequeNo", chequeNo, ChequeNoMax, "ChequeNo");
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

    private static string English(string key, params object[] args) =>
        string.Format(CultureInfo.InvariantCulture, Messages[key], args);

    private static void Required(
        Dictionary<string, string[]> errors, ValidationText text, string key, string? value, int max, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[key] = [text("Validation.Required", text("Validation.Field." + field))];
        }
        else if (value.Trim().Length > max)
        {
            errors[key] = [text("Validation.TooLong", text("Validation.Field." + field), max)];
        }
    }

    private static void Optional(
        Dictionary<string, string[]> errors, ValidationText text, string key, string? value, int max, string field)
    {
        if (value is not null && value.Trim().Length > max)
        {
            errors[key] = [text("Validation.TooLong", text("Validation.Field." + field), max)];
        }
    }

    private static void Phone(Dictionary<string, string[]> errors, ValidationText text, string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return;
        }

        var p = phone.Trim();
        if (p.Length > PhoneMax || !p.All(c => char.IsAsciiDigit(c) || c is '+' or '-' or ' ' or '(' or ')'))
        {
            errors["phone"] = [text("Validation.Phone")];
        }
    }
}
