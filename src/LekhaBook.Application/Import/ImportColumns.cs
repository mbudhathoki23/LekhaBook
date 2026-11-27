using static LekhaBook.Application.Import.ImportField;

namespace LekhaBook.Application.Import;

/// <summary>Finds the heading row of a table and guesses what each column holds. The user can change every guess.</summary>
public static class ImportColumns
{
    private const int HeaderSearchRows = 30;

    public static IReadOnlyList<ImportField> FieldsFor(ImportKind kind) => kind switch
    {
        ImportKind.Parties => [Name, PartyType, Phone, Email, Address, Pan, Balance, ToReceive, ToGive, Date],
        ImportKind.Entries => [Date, ImportField.Type, Name, Amount, MoneyIn, MoneyOut, Account, ToAccount, Category, Remarks, ChequeNo],
        _ => [Date, MoneyIn, MoneyOut, Amount, ImportField.Type, Name, Category, Account, Remarks, ChequeNo],
    };

    // Headings seen in bookkeeping apps and bank statements, in English and Nepali, normalized.
    // For a bank statement and a party ledger alike, "debit" is money going out and "credit" money coming in.
    private static readonly (ImportField Field, string[] Words)[] Synonyms =
    [
        (Date, ["date", "entrydate", "transactiondate", "txndate", "trandate", "valuedate", "billdate", "datead", "datebs", "miti", "asof", "asofdate", "openingdate", "मिति"]),
        (Name, ["name", "partyname", "party", "fullname", "customername", "customer", "suppliername", "supplier", "contactname", "partyaccount", "नाम", "पार्टी", "ग्राहक"]),
        (PartyType, ["partytype", "contacttype", "customersupplier"]),
        (Phone, ["phone", "phoneno", "phonenumber", "mobile", "mobileno", "mobilenumber", "contact", "contactno", "contactnumber", "फोन", "मोबाइल", "सम्पर्क"]),
        (Email, ["email", "emailaddress", "emailid", "इमेल"]),
        (Address, ["address", "location", "ठेगाना"]),
        (Pan, ["pan", "panno", "pannumber", "panvat", "vatno", "vatpanno", "panvatno", "gstin"]),
        (Balance, ["balance", "openingbalance", "currentbalance", "closingbalance", "dueamount", "netbalance", "due", "बाँकी", "मौज्दात"]),
        (ToReceive, ["toreceive", "receivable", "youwillget", "toreceiveamount", "receivableamount", "लिनुपर्ने", "पाउनुपर्ने"]),
        (ToGive, ["togive", "payable", "youwillgive", "togiveamount", "payableamount", "दिनुपर्ने", "तिर्नुपर्ने"]),
        (ImportField.Type, ["type", "transactiontype", "txntype", "entrytype", "vouchertype", "प्रकार"]),
        (Amount, ["amount", "amountrs", "amountnpr", "total", "totalamount", "value", "रकम"]),
        (MoneyIn, ["moneyin", "in", "cashin", "received", "receipt", "receipts", "deposit", "deposits", "depositamount", "credit", "cr", "creditamount", "amountin", "yougot", "got", "gotreduced", "paymentin", "जम्मा"]),
        (MoneyOut, ["moneyout", "out", "cashout", "paid", "payments", "withdraw", "withdrawal", "withdrawals", "withdrawalamount", "debit", "dr", "debitamount", "amountout", "yougave", "gave", "gaveadded", "paymentout", "खर्चरकम"]),
        (Account, ["account", "accountname", "paymentmethod", "paymentmode", "paymenttype", "mode", "bank", "bankname", "fromaccount", "खाता"]),
        (ToAccount, ["toaccount", "transferto"]),
        (Category, ["category", "expensecategory", "incomecategory", "head", "expensehead", "group", "शीर्षक"]),
        (Remarks, ["remarks", "remark", "description", "narration", "note", "notes", "details", "detail", "memo", "particulars", "comment", "कैफियत", "विवरण"]),
        (ChequeNo, ["chequeno", "chequenumber", "cheque", "checkno", "checknumber", "chqno", "chequerefno", "चेकनं", "चेकनम्बर"]),
    ];

    /// <summary>
    /// The row that most looks like headings (searched in the first lines, since exports often start with a title),
    /// or -1 when none does.
    /// </summary>
    /// <param name="extra">More headings, such as this app's own in the screen's language.</param>
    public static int DetectHeaderRow(
        IReadOnlyList<IReadOnlyList<string>> rows, ImportKind kind, IReadOnlyDictionary<string, ImportField>? extra = null)
    {
        var best = -1;
        var bestScore = 1; // at least two known headings
        for (var i = 0; i < Math.Min(rows.Count, HeaderSearchRows); i++)
        {
            var fields = Guess(rows[i], kind, extra);
            var score = fields.Count(f => f != Skip);

            // A statement may have only a date column we know besides amounts; parties may only have a name.
            if (score == 1 && kind == ImportKind.Parties && fields.Contains(Name))
            {
                score = 2;
            }

            if (score > bestScore)
            {
                (best, bestScore) = (i, score);
            }
        }

        return best;
    }

    /// <summary>A field for each heading of <paramref name="header"/>; each field is used at most once.</summary>
    public static IReadOnlyList<ImportField> Guess(
        IReadOnlyList<string> header, ImportKind kind, IReadOnlyDictionary<string, ImportField>? extra = null)
    {
        var allowed = FieldsFor(kind);
        var words = header.Select(ImportValues.Normalize).ToArray();
        var result = new ImportField[header.Count];
        var priority = new int[header.Count];

        // The best synonym of each column; earlier words in a field's list are the surer ones.
        for (var column = 0; column < words.Length; column++)
        {
            var word = words[column];
            if (word.Length == 0)
            {
                continue;
            }

            if (extra is not null && extra.TryGetValue(word, out var named) && allowed.Contains(named))
            {
                (result[column], priority[column]) = (named, -1);
                continue;
            }

            foreach (var (field, list) in Synonyms)
            {
                var at = Array.IndexOf(list, word);
                if (at >= 0 && allowed.Contains(field))
                {
                    (result[column], priority[column]) = (field, at);
                    break;
                }
            }
        }

        // "Particulars" names the entry's type when another column already holds the details.
        if (kind != ImportKind.Parties && !result.Contains(ImportField.Type))
        {
            var particulars = Array.IndexOf(words, "particulars");
            if (particulars >= 0 && result.Where((f, i) => f == Remarks && i != particulars).Any())
            {
                (result[particulars], priority[particulars]) = (ImportField.Type, 0);
            }
        }

        // When two columns claim a field, the surer heading keeps it.
        foreach (var group in Enumerable.Range(0, result.Length).Where(i => result[i] != Skip).GroupBy(i => result[i]))
        {
            foreach (var column in group.OrderBy(i => priority[i]).ThenBy(i => i).Skip(1))
            {
                result[column] = Skip;
            }
        }

        return result;
    }
}
