using LekhaBook.Contracts;
using LekhaBook.Domain;
using static LekhaBook.Domain.TransactionType;

namespace LekhaBook.Application.Import;

/// <summary>
/// Decides what every row of a table would become. Nothing is guessed silently: a row that can't be read is an
/// error with a reason, and anything that looks already entered is left unselected.
/// </summary>
public static class ImportPlanner
{
    /// <summary>The entry types an import can add; adjustments have no screen yet and are not imported.</summary>
    public static readonly IReadOnlyList<TransactionType> EntryTypes = [PaymentIn, PaymentOut, Expense, OtherIncome, TransferMoney];

    private static readonly Dictionary<string, TransactionType> Words = Normalized(new()
    {
        [PaymentIn] = ["payment in", "receipt", "received", "money received", "payment received", "you got", "got", "भुक्तानी प्राप्त"],
        [PaymentOut] = ["payment out", "payment", "paid", "payment made", "you gave", "gave", "भुक्तानी गरिएको"],
        [Expense] = ["expense", "expenses", "खर्च"],
        [OtherIncome] = ["income", "other income", "आम्दानी"],
        [TransferMoney] = ["transfer", "transfer money", "money transfer", "contra", "रकम ट्रान्सफर"],
    });

    // Kinds other apps have that move no money here, or that this app can't add yet.
    private static readonly HashSet<string> Unsupported = new(new[]
    {
        "sales", "sale", "sales invoice", "purchase", "purchases", "purchase invoice", "sales return", "purchase return",
        "credit note", "debit note", "quotation", "add balance", "reduce balance", "adjustment", "add money", "reduce money",
        "party adjustment",
    }.Select(ImportValues.Normalize));

    private static readonly HashSet<string> TotalWords = new(new[]
    {
        "total", "totals", "grand total", "sub total", "closing balance", "balance c/d", "total balance", "जम्मा", "कुल जम्मा",
    }.Select(ImportValues.Normalize));

    private static readonly HashSet<string> OpeningWords = new(new[]
    {
        "opening balance", "balance b/d", "balance b/f", "brought forward", "opening", "सुरु मौज्दात", "शुरु मौज्दात",
    }.Select(ImportValues.Normalize));

    public static ImportPlan Build(IReadOnlyList<IReadOnlyList<string>> rows, ImportOptions options, ImportBooks books)
    {
        var builder = new Builder(rows, options, books);
        for (var i = options.HeaderRow + 1; i < rows.Count; i++)
        {
            builder.Add(i);
        }

        return builder.Plan();
    }

    private static Dictionary<string, TransactionType> Normalized(Dictionary<TransactionType, string[]> words) =>
        words.SelectMany(p => p.Value.Append(p.Key.ToString()).Select(w => (Word: ImportValues.Normalize(w), p.Key)))
            .DistinctBy(p => p.Word)
            .ToDictionary(p => p.Word, p => p.Key);

    private sealed record Named(Guid Id, string Name);

    /// <summary>One planning run: the lookups, and what earlier rows have already used.</summary>
    private sealed class Builder
    {
        private readonly IReadOnlyList<IReadOnlyList<string>> _rows;
        private readonly ImportOptions _options;
        private readonly ImportBooks _books;
        private readonly ImportDateOrder _order;
        private readonly string? _header;

        private readonly List<ImportRow> _result = [];
        private readonly List<SavePartyRequest> _newParties = [];
        private readonly List<string> _unknownTypes = [];

        private readonly Dictionary<string, Named> _parties = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Named> _accounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CategoryDto> _categories = new(StringComparer.OrdinalIgnoreCase);

        // Entries already in the books, counted, so two identical rows need two identical entries to both be duplicates.
        private readonly Dictionary<(DateOnly, TransactionType, decimal, Guid, Guid), int> _existing = [];
        private readonly Dictionary<(DateOnly, decimal, Guid Account, bool In), int> _existingByAccount = [];

        public Builder(IReadOnlyList<IReadOnlyList<string>> rows, ImportOptions options, ImportBooks books)
        {
            (_rows, _options, _books) = (rows, options, books);
            _header = options.HeaderRow >= 0 && options.HeaderRow < rows.Count ? Joined(rows[options.HeaderRow]) : null;
            _order = options.DateOrder != ImportDateOrder.Auto
                ? options.DateOrder
                : ImportValues.DetectOrder(rows.Skip(options.HeaderRow + 1).Select(r => Cell(r, ImportField.Date)));

            foreach (var party in books.Parties)
            {
                _parties.TryAdd(Key(party.Name), new Named(party.Id, party.Name));
            }

            foreach (var account in books.Accounts)
            {
                _accounts.TryAdd(Key(account.Name), new Named(account.Id, account.Name));
            }

            foreach (var category in books.Categories)
            {
                _categories.TryAdd($"{category.Type}:{Key(category.Name)}", category);
            }

            foreach (var entry in books.Entries.Where(e => e.Status == nameof(TransactionStatus.Posted)))
            {
                if (!Enum.TryParse<TransactionType>(entry.Type, out var type))
                {
                    continue;
                }

                Count(_existing, (entry.Date, type, entry.Amount, entry.FromAccountId, entry.ToAccountId));
                Count(_existingByAccount, (entry.Date, entry.Amount, entry.ToAccountId, true));
                Count(_existingByAccount, (entry.Date, entry.Amount, entry.FromAccountId, false));
            }
        }

        public ImportPlan Plan() => new(_result, _newParties, _unknownTypes);

        public void Add(int index)
        {
            var cells = _rows[index];
            if (cells.All(string.IsNullOrWhiteSpace))
            {
                return;
            }

            // A report that spans pages repeats its headings.
            if (_header is not null && Joined(cells) == _header)
            {
                return;
            }

            var row = new ImportRow { Line = index + 1, Detail = Preview(cells) };
            _result.Add(row);
            if (_options.Kind == ImportKind.Parties)
            {
                AddParty(row, cells);
            }
            else
            {
                AddEntry(row, cells);
            }

            row.Selected = row.Status == ImportRowStatus.Ready;
        }

        // ---------------- Parties ----------------

        private void AddParty(ImportRow row, IReadOnlyList<string> cells)
        {
            var name = Cell(cells, ImportField.Name);
            if (name.Length == 0 || TotalWords.Contains(ImportValues.Normalize(name)))
            {
                Skip(row, "Import.Row.NotData");
                return;
            }

            row.Title = name;
            if (!TryOpening(row, cells, out var opening, out var side))
            {
                return;
            }

            DateOnly? asOf = null;
            if (Cell(cells, ImportField.Date) is { Length: > 0 } dateText)
            {
                if (!TryDate(row, dateText, out var date))
                {
                    return;
                }

                asOf = date;
            }

            var type = ImportValues.Normalize(Cell(cells, ImportField.PartyType)) switch
            {
                "customer" or "ग्राहक" => Domain.PartyType.Customer,
                "supplier" or "vendor" or "आपूर्तिकर्ता" => Domain.PartyType.Supplier,
                _ => _options.DefaultPartyType,
            };

            var request = new SavePartyRequest(
                name,
                type.ToString(),
                CleanPhone(Cell(cells, ImportField.Phone)),
                MasterValidation.Clean(Cell(cells, ImportField.Email)),
                MasterValidation.Clean(Cell(cells, ImportField.Address)),
                MasterValidation.Clean(Cell(cells, ImportField.Pan)),
                OpeningBalance: opening,
                OpeningSide: MasterValidation.OpeningSideName(side),
                OpeningAsOf: asOf,
                Id: Guid.CreateVersion7());

            row.Party = request;
            row.Date = asOf;
            row.Amount = side == BalanceSide.Debit ? opening : -opening;
            row.Detail = string.Join(" · ", new[] { request.Phone, request.Address }.Where(v => v is not null));

            if (MasterValidation.Validate(request, _options.Text) is { Count: > 0 } errors)
            {
                Fail(row, "Import.Row.Invalid", string.Join(" ", errors.Values.SelectMany(v => v)));
            }
            else if (!_parties.TryAdd(Key(name), new Named(request.Id!.Value, name)))
            {
                row.Status = ImportRowStatus.Duplicate;
                row.Note = new ImportNote("Import.Row.PartyExists");
            }
        }

        private bool TryOpening(ImportRow row, IReadOnlyList<string> cells, out decimal opening, out BalanceSide side)
        {
            (opening, side) = (0, BalanceSide.Debit);
            var receive = Cell(cells, ImportField.ToReceive);
            var give = Cell(cells, ImportField.ToGive);
            var balance = Cell(cells, ImportField.Balance);

            decimal signed = 0;
            foreach (var (text, sign) in new[] { (receive, 1), (give, -1) })
            {
                if (ImportValues.IsBlank(text))
                {
                    continue;
                }

                if (!ImportValues.TryAmount(text, out var amount))
                {
                    return Fail(row, "Import.Row.BadAmount", text);
                }

                signed += sign * amount.Value;
            }

            // The two columns win; the balance column is only read when neither has anything.
            if (signed == 0 && !ImportValues.IsBlank(balance))
            {
                var word = ImportValues.Normalize(balance);
                var givenSide = 0;
                foreach (var (suffix, sign) in new[] { ("To Receive", 1), ("Receivable", 1), ("To Give", -1), ("Payable", -1) })
                {
                    if (word.EndsWith(ImportValues.Normalize(suffix), StringComparison.Ordinal))
                    {
                        givenSide = sign;
                        balance = balance.Replace(suffix, string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
                        break;
                    }
                }

                if (!ImportValues.TryAmount(balance, out var amount))
                {
                    return Fail(row, "Import.Row.BadAmount", balance);
                }

                var negative = givenSide != 0 ? givenSide < 0 : amount.DrCr == 'C' || amount.Negative;
                signed = negative ? -amount.Value : amount.Value;
            }

            opening = Math.Abs(signed);
            side = signed < 0 ? BalanceSide.Credit : BalanceSide.Debit;
            return true;
        }

        // ---------------- Entries ----------------

        private void AddEntry(ImportRow row, IReadOnlyList<string> cells)
        {
            var dateText = Cell(cells, ImportField.Date);
            var typeText = Cell(cells, ImportField.Type);
            var hash = typeText.IndexOf('#'); // "Payment In #12" carries the voucher number
            if (hash > 0)
            {
                typeText = typeText[..hash].Trim();
            }

            var remarks = Cell(cells, ImportField.Remarks);
            if (new[] { typeText, remarks, Cell(cells, ImportField.Name), dateText }
                .Any(v => OpeningWords.Contains(ImportValues.Normalize(v))))
            {
                Skip(row, "Import.Row.Opening");
                return;
            }

            // A total line has no date; "जम्मा" on a dated row means a deposit, not a total.
            if (dateText.Length == 0
                    ? !HasAmount(cells) || cells.Take(3).Any(v => TotalWords.Contains(ImportValues.Normalize(v)))
                    : TotalWords.Contains(ImportValues.Normalize(dateText)))
            {
                Skip(row, "Import.Row.NotData");
                return;
            }

            // The type word first, so an unsupported kind is reported as that and not as a missing value.
            TransactionType? type = null;
            if (typeText.Length > 0 && !TryType(row, typeText, out type))
            {
                return;
            }

            if (dateText.Length == 0)
            {
                Fail(row, "Import.Row.NoDate");
                return;
            }

            if (!TryDate(row, dateText, out var date))
            {
                return;
            }

            row.Date = date;
            if (date > _books.Today)
            {
                Fail(row, "Import.Row.FutureDate");
                return;
            }

            if (!TryMoney(row, cells, out var amount, out var moneyIn))
            {
                return;
            }

            row.Amount = amount;
            if (!PostingRules.IsValidAmount(amount))
            {
                Fail(row, "Import.Row.BadAmount", amount);
                return;
            }

            // The chosen type fills gaps only: it never turns money the file shows coming in into money going out.
            if (type is null && _options.DefaultType is { } preset && (moneyIn is null || moneyIn == IsMoneyIn(preset)))
            {
                type = preset;
            }

            if (_options.Kind == ImportKind.Statement)
            {
                AddStatementLine(row, cells, date, amount, moneyIn, type);
            }
            else
            {
                AddListedEntry(row, cells, date, amount, moneyIn, type);
            }
        }

        /// <summary>A row of a many-party report: the row says its own type, party and payment method.</summary>
        private void AddListedEntry(
            ImportRow row, IReadOnlyList<string> cells, DateOnly date, decimal amount, bool? moneyIn, TransactionType? type)
        {
            var name = Cell(cells, ImportField.Name);
            var category = Cell(cells, ImportField.Category);
            type ??= moneyIn switch
            {
                true => name.Length > 0 ? PaymentIn : OtherIncome,
                false => name.Length > 0 ? PaymentOut : Expense,
                null when _categories.ContainsKey($"Expense:{Key(category)}") => Expense,
                null when _categories.ContainsKey($"Income:{Key(category)}") => OtherIncome,
                null => null,
            };

            if (type is not { } chosen)
            {
                Fail(row, "Import.Row.NoType");
                return;
            }

            row.Type = chosen;
            Named? from;
            Named? to;
            string? extraRemark = null;
            if (chosen == TransferMoney)
            {
                from = MoneyAccount(row, Cell(cells, ImportField.Account));
                to = from is null ? null : MoneyAccount(row, Cell(cells, ImportField.ToAccount));
            }
            else
            {
                var money = MoneyAccount(row, Cell(cells, ImportField.Account));
                if (money is null)
                {
                    return;
                }

                var other = chosen is PaymentIn or PaymentOut
                    ? Party(row, name)
                    : Head(row, category.Length > 0 ? category : name, chosen == Expense, out extraRemark);
                (from, to) = chosen is PaymentOut or Expense ? (money, other) : (other, money);
            }

            if (from is null || to is null || !Finish(row, cells, chosen, date, amount, from, to, extraRemark))
            {
                return;
            }

            MarkDuplicate(row, _existing, (date, chosen, amount, from.Id, to.Id));
        }

        /// <summary>A line of one account's statement: the chosen account is always one side.</summary>
        private void AddStatementLine(
            ImportRow row, IReadOnlyList<string> cells, DateOnly date, decimal amount, bool? moneyIn, TransactionType? type)
        {
            if (type == TransferMoney)
            {
                Skip(row, "Import.Row.StatementTransfer");
                return;
            }

            // The type word may give the direction when the amounts don't.
            moneyIn ??= type is { } given ? IsMoneyIn(given) : null;
            if (moneyIn is not { } isIn)
            {
                Fail(row, "Import.Row.NoDirection");
                return;
            }

            if (type is { } stated && IsMoneyIn(stated) != isIn)
            {
                Fail(row, "Import.Row.DirectionClash");
                return;
            }

            if (_options.StatementAccountId is not { } statementId)
            {
                Fail(row, "Import.Row.ChooseStatement");
                return;
            }

            Named? from;
            Named? to;
            string? extraRemark = null;
            var byAccountOnly = false;
            if (_books.Parties.FirstOrDefault(p => p.Id == statementId) is { } party)
            {
                if (MoneyAccount(row, Cell(cells, ImportField.Account)) is not { } money)
                {
                    return;
                }

                var self = new Named(party.Id, party.Name);
                row.Type = isIn ? PaymentIn : PaymentOut;
                (from, to) = isIn ? (self, money) : (money, self);
            }
            else if (_books.Accounts.FirstOrDefault(a => a.Id == statementId) is { } account)
            {
                var self = new Named(account.Id, account.Name);
                var name = Cell(cells, ImportField.Name);
                Named? other;

                // A name that is no known party is only a payer's name on a bank statement, unless parties may be added.
                var usesParty = type is PaymentIn or PaymentOut
                    || (type is null && name.Length > 0 && (_parties.ContainsKey(Key(name)) || _options.AddMissingParties));
                if (usesParty)
                {
                    row.Type = isIn ? PaymentIn : PaymentOut;
                    other = Party(row, name);
                }
                else
                {
                    row.Type = isIn ? OtherIncome : Expense;
                    var category = Cell(cells, ImportField.Category);
                    other = Head(row, category.Length > 0 ? category : name, !isIn, out extraRemark);
                    byAccountOnly = true;
                }

                (from, to) = isIn ? (other, self) : (self, other);
            }
            else
            {
                Fail(row, "Import.Row.ChooseStatement");
                return;
            }

            if (from is null || to is null || !Finish(row, cells, row.Type!.Value, date, amount, from, to, extraRemark))
            {
                return;
            }

            if (byAccountOnly)
            {
                // The category of an already entered line may differ, so match on the account, day, amount and direction.
                MarkDuplicate(row, _existingByAccount, (date, amount, statementId, isIn));
            }
            else
            {
                MarkDuplicate(row, _existing, (date, row.Type!.Value, amount, from.Id, to.Id));
            }
        }

        private bool Finish(
            ImportRow row, IReadOnlyList<string> cells, TransactionType type, DateOnly date, decimal amount,
            Named from, Named to, string? extraRemark)
        {
            var remarks = string.Join(" · ", new[] { extraRemark, Cell(cells, ImportField.Remarks) }
                .Where(v => !string.IsNullOrWhiteSpace(v)));
            if (remarks.Length > MasterValidation.RemarksMax)
            {
                remarks = remarks[..(MasterValidation.RemarksMax - 1)] + "…";
                row.Note = new ImportNote("Import.Row.RemarksShortened");
            }

            var request = new CreateTransactionRequest(
                type.ToString(), date, amount, from.Id, to.Id,
                MasterValidation.Clean(remarks),
                MasterValidation.Clean(Cell(cells, ImportField.ChequeNo) is var cheque && ImportValues.IsBlank(cheque) ? null : cheque),
                Id: Guid.CreateVersion7());

            row.Entry = request;
            row.Title = $"{from.Name} → {to.Name}";
            row.Detail = request.Remarks;

            if (from.Id == to.Id)
            {
                return Fail(row, "Import.Row.SameAccount");
            }

            if (MasterValidation.Validate(request, _options.Text) is { Count: > 0 } errors)
            {
                return Fail(row, "Import.Row.Invalid", string.Join(" ", errors.Values.SelectMany(v => v)));
            }

            return true;
        }

        private static void MarkDuplicate<TKey>(ImportRow row, Dictionary<TKey, int> counts, TKey key)
            where TKey : notnull
        {
            if (counts.TryGetValue(key, out var left) && left > 0)
            {
                counts[key] = left - 1;
                row.Status = ImportRowStatus.Duplicate;
                row.Note = new ImportNote("Import.Row.EntryExists");
            }
        }

        // ---------------- Cells ----------------

        private static bool IsMoneyIn(TransactionType type) => type is PaymentIn or OtherIncome;

        private bool TryType(ImportRow row, string text, out TransactionType? type)
        {
            type = null;
            var word = ImportValues.Normalize(text);
            if (_options.TypeChoices.TryGetValue(text, out var choice))
            {
                Remember(text);
                if (choice is null)
                {
                    Skip(row, "Import.Row.LeftOut", text);
                    return false;
                }

                type = choice;
                return true;
            }

            if (_options.TypeNames.TryGetValue(word, out var named) && EntryTypes.Contains(named))
            {
                type = named;
                return true;
            }

            if (Words.TryGetValue(word, out var known))
            {
                type = known;
                return true;
            }

            Remember(text);
            if (Unsupported.Contains(word) || _options.TypeNames.ContainsKey(word))
            {
                Skip(row, "Import.Row.Unsupported", text);
            }
            else
            {
                Fail(row, "Import.Row.UnknownType", text);
            }

            return false;
        }

        private void Remember(string typeWord)
        {
            if (!_unknownTypes.Contains(typeWord, StringComparer.OrdinalIgnoreCase))
            {
                _unknownTypes.Add(typeWord);
            }
        }

        private bool TryDate(ImportRow row, string text, out DateOnly date) =>
            ImportValues.TryDate(text, _options.Calendar, _order, out date) || Fail(row, "Import.Row.BadDate", text);

        private bool HasAmount(IReadOnlyList<string> cells) =>
            new[] { ImportField.Amount, ImportField.MoneyIn, ImportField.MoneyOut }
                .Any(f => !ImportValues.IsBlank(Cell(cells, f)));

        /// <summary>
        /// The amount, and the direction when the file says it: separate in and out columns, or a minus sign or
        /// Dr/Cr in a single amount column.
        /// </summary>
        private bool TryMoney(ImportRow row, IReadOnlyList<string> cells, out decimal amount, out bool? moneyIn)
        {
            (amount, moneyIn) = (0, null);
            var read = new Dictionary<ImportField, ImportAmount>();
            foreach (var field in new[] { ImportField.MoneyIn, ImportField.MoneyOut, ImportField.Amount })
            {
                var text = Cell(cells, field);
                if (ImportValues.IsBlank(text))
                {
                    continue;
                }

                if (!ImportValues.TryAmount(text, out var value))
                {
                    return Fail(row, "Import.Row.BadAmount", text);
                }

                if (value.Value != 0)
                {
                    read[field] = value;
                }
            }

            if (read.ContainsKey(ImportField.MoneyIn) && read.ContainsKey(ImportField.MoneyOut))
            {
                return Fail(row, "Import.Row.BothDirections");
            }

            if (read.TryGetValue(ImportField.MoneyIn, out var money))
            {
                (amount, moneyIn) = (money.Value, !money.Negative);
            }
            else if (read.TryGetValue(ImportField.MoneyOut, out money))
            {
                (amount, moneyIn) = (money.Value, money.Negative);
            }
            else if (read.TryGetValue(ImportField.Amount, out money))
            {
                amount = money.Value;
                moneyIn = money.DrCr switch
                {
                    'C' => true,
                    'D' => false,
                    _ => money.Negative ? false : SignedAmounts ? true : null,
                };
            }
            else
            {
                return Fail(row, "Import.Row.NoAmount");
            }

            if (_options.SwapInOut)
            {
                moneyIn = !moneyIn;
            }

            return true;
        }

        private bool? _signedAmounts;

        /// <summary>An amount column with some negative values is signed, so the positive ones are money in.</summary>
        private bool SignedAmounts => _signedAmounts ??= _rows.Skip(_options.HeaderRow + 1)
            .Any(r => ImportValues.TryAmount(Cell(r, ImportField.Amount), out var a) && a.Negative);

        private Named? MoneyAccount(ImportRow row, string name)
        {
            if (name.Length > 0)
            {
                if (_accounts.TryGetValue(Key(name), out var account))
                {
                    return account;
                }

                Fail(row, "Import.Row.NoAccountNamed", name);
                return null;
            }

            if (_books.Accounts.FirstOrDefault(a => a.Id == _options.DefaultMoneyAccountId) is { } fallback)
            {
                return new Named(fallback.Id, fallback.Name);
            }

            Fail(row, "Import.Row.ChooseAccount");
            return null;
        }

        private Named? Party(ImportRow row, string name)
        {
            if (name.Length == 0)
            {
                Fail(row, "Import.Row.NoParty");
                return null;
            }

            if (_parties.TryGetValue(Key(name), out var party))
            {
                if (_newParties.Any(p => p.Id == party.Id))
                {
                    row.NewPartyId = party.Id;
                    row.Note ??= new ImportNote("Import.Row.NewParty", party.Name);
                }

                return party;
            }

            if (!_options.AddMissingParties)
            {
                Fail(row, "Import.Row.NoPartyNamed", name);
                return null;
            }

            var request = new SavePartyRequest(name, _options.DefaultPartyType.ToString(), Id: Guid.CreateVersion7());
            if (MasterValidation.Validate(request, _options.Text) is { Count: > 0 } errors)
            {
                Fail(row, "Import.Row.Invalid", string.Join(" ", errors.Values.SelectMany(v => v)));
                return null;
            }

            _newParties.Add(request);
            var added = new Named(request.Id!.Value, name);
            _parties[Key(name)] = added;
            row.NewPartyId = added.Id;
            row.Note = new ImportNote("Import.Row.NewParty", name);
            return added;
        }

        /// <summary>
        /// The expense or income head named by the row, else the chosen default; a name that matched no head is kept
        /// in the remarks.
        /// </summary>
        private Named? Head(ImportRow row, string name, bool expense, out string? extraRemark)
        {
            extraRemark = null;
            var type = expense ? "Expense" : "Income";
            if (_categories.TryGetValue($"{type}:{Key(name)}", out var head))
            {
                return new Named(head.Id, head.Name);
            }

            var defaultId = expense ? _options.DefaultExpenseId : _options.DefaultIncomeId;
            if (_books.Categories.FirstOrDefault(c => c.Id == defaultId && c.Type == type) is not { } fallback)
            {
                Fail(row, "Import.Row.ChooseCategory");
                return null;
            }

            extraRemark = name.Length > 0 ? name : null;
            return new Named(fallback.Id, fallback.Name);
        }

        private string Cell(IReadOnlyList<string> cells, ImportField field)
        {
            for (var i = 0; i < _options.Columns.Count && i < cells.Count; i++)
            {
                if (_options.Columns[i] == field)
                {
                    return cells[i].Trim();
                }
            }

            return string.Empty;
        }

        private static void Skip(ImportRow row, string key, params object[] args)
        {
            row.Status = ImportRowStatus.Skipped;
            row.Note = new ImportNote(key, args);
        }

        private static bool Fail(ImportRow row, string key, params object[] args)
        {
            row.Status = ImportRowStatus.Error;
            row.Note = new ImportNote(key, args);
            return false;
        }

        private static void Count<TKey>(Dictionary<TKey, int> counts, TKey key)
            where TKey : notnull =>
            counts[key] = counts.GetValueOrDefault(key) + 1;

        /// <summary>Names match ignoring case and extra spaces.</summary>
        private static string Key(string name) => string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        private static string Joined(IReadOnlyList<string> cells) => string.Join('|', cells.Select(c => c.Trim()));

        private static string Preview(IReadOnlyList<string> cells) => string.Join(" · ", cells.Where(c => !string.IsNullOrWhiteSpace(c)));

        /// <summary>Spreadsheets add ".0" to numbers, and exports join several numbers; keep what the app accepts.</summary>
        private static string? CleanPhone(string value)
        {
            var phone = ImportValues.AsciiDigits(value).Trim();
            if (phone.EndsWith(".0", StringComparison.Ordinal))
            {
                phone = phone[..^2];
            }

            return ImportValues.IsBlank(phone) ? null : phone;
        }
    }
}
