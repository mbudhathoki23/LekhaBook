using System.Text.Json;
using LekhaBook.Application;
using LekhaBook.Contracts;
using LekhaBook.Data.Postgres;
using LekhaBook.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LekhaBook.Api.Endpoints;

internal enum WriteStatus
{
    Created,
    Saved,

    /// <summary>Already applied (a retry): nothing was written.</summary>
    Replayed,
    Invalid,
    Forbidden,
    Conflict,
    PossibleDuplicate,
    NotFound,
}

/// <summary>What a write did, without saying how to report it: REST turns it into a status code, sync into an item result.</summary>
internal sealed record WriteResult(
    WriteStatus Status, SyncEntity? Row = null, string? Error = null, IReadOnlyDictionary<string, string[]>? Errors = null,
    IReadOnlyList<Transaction>? Duplicates = null)
{
    public bool Ok => Status is WriteStatus.Created or WriteStatus.Saved or WriteStatus.Replayed;

    public static WriteResult Invalid(string field, string message) =>
        Invalid(new Dictionary<string, string[]> { [field] = [message] });

    public static WriteResult Invalid(IReadOnlyDictionary<string, string[]> errors) =>
        new(WriteStatus.Invalid, Error: string.Join(" ", errors.Values.SelectMany(v => v)), Errors: errors);

    public static WriteResult Forbidden(string message) => new(WriteStatus.Forbidden, Error: message);

    public static WriteResult Conflict(string message) => new(WriteStatus.Conflict, Error: message);
}

/// <summary>
/// The only place transactions and party categories are written, so a normal save and a device's sync push follow
/// the same rules (D15, D17): accounts are loaded from the company only, <see cref="PostingRules"/> decides which
/// fit, <see cref="EntryRules"/> who may write, every write gets a server version and transactions are audited.
/// <c>expectedVersion</c> is the server version a change was based on; null (online saves) means last writer wins.
/// </summary>
internal sealed class BooksWriter(PostgresDbContext db, TimeProvider clock, Guid companyId, CompanyMember member)
{
    private const string ChangedElsewhere = "This entry was changed elsewhere since you opened it.";

    public async Task<WriteResult> CreateTransactionAsync(CreateTransactionRequest request)
    {
        if (MasterValidation.Validate(request) is { Count: > 0 } errors)
        {
            return WriteResult.Invalid(errors);
        }

        var type = MasterValidation.ParseTransactionType(request.Type)!.Value;

        // A retried create (same client-generated id) returns what the first attempt saved.
        if (request.Id is { } requestedId && await FindTransactionByIdAsync(requestedId) is { } existing)
        {
            return Replay(existing);
        }

        var (_, today) = await TodayAsync();
        var date = request.Date ?? today;
        if (date > today)
        {
            return WriteResult.Invalid("date", "The date can't be in the future.");
        }

        if (EntryRules.CheckCreate(member, type, date, today) is { } denied)
        {
            return WriteResult.Forbidden(denied);
        }

        var id = request.Id ?? Guid.CreateVersion7();
        var transaction = new Transaction
        {
            Id = id,
            CompanyId = companyId,
            Type = type,
            VoucherNo = id.ToString(), // provisional until voucher numbering (D15)
            IsProvisionalNo = true,
            Date = date,
            Amount = request.Amount,
            FromAccountId = request.FromAccountId,
            ToAccountId = request.ToAccountId,
            Remarks = MasterValidation.Clean(request.Remarks),
            ChequeNo = MasterValidation.Clean(request.ChequeNo),
            ChequeDate = request.ChequeDate,
        };
        if (await PostingProblemAsync(transaction) is { } problem)
        {
            return problem;
        }

        db.Transactions.Add(transaction);
        Audit(transaction, "Create", before: null);
        try
        {
            if (await SaveTransactionAsync(transaction, request.CheckForDuplicates) is { } blocked)
            {
                // Another copy of this same request may have saved while this one waited for the company lock.
                return await FindTransactionByIdAsync(id) is { } saved ? Replay(saved) : blocked;
            }
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The same create arrived twice at once: the other request saved it first.
            db.ChangeTracker.Clear();
            if (await FindTransactionByIdAsync(id) is not { } saved)
            {
                throw;
            }

            return Replay(saved);
        }

        return new WriteResult(WriteStatus.Created, transaction);
    }

    public async Task<WriteResult> UpdateTransactionAsync(Guid transactionId, UpdateTransactionRequest request, long? expectedVersion)
    {
        var transaction = await FindTransactionAsync(transactionId);
        if (transaction is null)
        {
            return new WriteResult(WriteStatus.NotFound, Error: "That entry doesn't exist.");
        }

        if (transaction.Status != TransactionStatus.Posted)
        {
            return expectedVersion is null
                ? new WriteResult(WriteStatus.NotFound, Error: "That entry was voided.")
                : WriteResult.Conflict("This entry was voided elsewhere.");
        }

        if (MasterValidation.Validate(request) is { Count: > 0 } errors)
        {
            return WriteResult.Invalid(errors);
        }

        if (request.Type is not null && MasterValidation.ParseTransactionType(request.Type) != transaction.Type)
        {
            return WriteResult.Invalid("type", "An entry's type can't be changed. Void it and add a new one.");
        }

        // A device that never got the reply sends the same change again.
        if (expectedVersion is not null && Matches(transaction, request))
        {
            return new WriteResult(WriteStatus.Replayed, transaction);
        }

        var (zone, today) = await TodayAsync();
        if (request.Date == default)
        {
            return WriteResult.Invalid("date", "A date is required.");
        }

        if (request.Date > today)
        {
            return WriteResult.Invalid("date", "The date can't be in the future.");
        }

        if (EntryRules.CheckChange(member, transaction, EntryAction.Edit, request.Date, today, zone) is { } denied)
        {
            return WriteResult.Forbidden(denied);
        }

        var basedOn = transaction.Version;
        if (expectedVersion is { } expected && expected != basedOn)
        {
            return WriteResult.Conflict(ChangedElsewhere);
        }

        var before = Snapshot(transaction);
        var checkDuplicates = request.CheckForDuplicates
            && (transaction.Date != request.Date || transaction.Amount != request.Amount
                || PartyId(transaction.Type, transaction.FromAccountId, transaction.ToAccountId)
                    != PartyId(transaction.Type, request.FromAccountId, request.ToAccountId));
        transaction.Date = request.Date;
        transaction.Amount = request.Amount;
        transaction.FromAccountId = request.FromAccountId;
        transaction.ToAccountId = request.ToAccountId;
        transaction.Remarks = MasterValidation.Clean(request.Remarks);
        transaction.ChequeNo = MasterValidation.Clean(request.ChequeNo);
        transaction.ChequeDate = request.ChequeDate;
        if (await PostingProblemAsync(transaction) is { } problem)
        {
            return problem;
        }

        Audit(transaction, "Update", before);
        return await SaveTransactionAsync(transaction, checkDuplicates, expectedVersion is null ? null : basedOn)
            ?? new WriteResult(WriteStatus.Saved, transaction);
    }

    /// <summary>"Delete" voids: the row is kept, and balances and statements skip it (D15).</summary>
    public async Task<WriteResult> VoidTransactionAsync(Guid transactionId, long? expectedVersion)
    {
        var transaction = await FindTransactionAsync(transactionId);
        if (transaction is null)
        {
            return new WriteResult(WriteStatus.NotFound, Error: "That entry doesn't exist.");
        }

        var alreadyVoid = transaction.Status == TransactionStatus.Void;
        if (alreadyVoid && member.Has(EntryRules.PermissionFor(transaction.Type, EntryAction.Delete)))
        {
            return new WriteResult(WriteStatus.Replayed, transaction); // a retried void
        }

        var (zone, today) = await TodayAsync();
        if (EntryRules.CheckChange(member, transaction, EntryAction.Delete, null, today, zone) is { } denied)
        {
            return WriteResult.Forbidden(denied);
        }

        var basedOn = transaction.Version;
        if (expectedVersion is { } expected && expected != basedOn)
        {
            return WriteResult.Conflict(ChangedElsewhere);
        }

        var before = Snapshot(transaction);
        transaction.Status = TransactionStatus.Void;
        Audit(transaction, "Void", before);
        return await SaveAsync(transaction, expectedVersion is null ? null : basedOn)
            ? new WriteResult(WriteStatus.Saved, transaction)
            : WriteResult.Conflict(ChangedElsewhere);
    }

    public async Task<WriteResult> CreateCategoryAsync(SavePartyCategoryRequest request)
    {
        if (!member.Has(Permission.SettingsFeatures))
        {
            return WriteResult.Forbidden("You don't have permission to add party categories.");
        }

        if (MasterValidation.Validate(request) is { Count: > 0 } errors)
        {
            return WriteResult.Invalid(errors);
        }

        if (request.Id is { } requestedId && await db.PartyCategories.AsNoTracking().SingleOrDefaultAsync(c => c.Id == requestedId) is { } existing)
        {
            return existing.CompanyId == companyId && !existing.IsDeleted
                ? new WriteResult(WriteStatus.Replayed, existing)
                : WriteResult.Conflict("That id is already in use.");
        }

        var name = request.Name.Trim();
        var names = await db.PartyCategories.AsNoTracking()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted)
            .Select(c => c.Name)
            .ToListAsync();
        if (names.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return WriteResult.Conflict($"The category \"{name}\" already exists.");
        }

        var category = new PartyCategory { Id = request.Id ?? Guid.CreateVersion7(), CompanyId = companyId, Name = name };
        db.PartyCategories.Add(category);
        await SaveAsync(category);
        return new WriteResult(WriteStatus.Created, category);
    }

    // ---------------- Parties (for sync push, same rules as MasterEndpoints) ----------------

    public async Task<WriteResult> CreatePartyAsync(SavePartyRequest request)
    {
        if (MasterValidation.Validate(request) is { Count: > 0 } errors)
        {
            return WriteResult.Invalid(errors);
        }

        var type = MasterValidation.ParsePartyType(request.PartyType)!.Value;
        var (_, today) = await TodayAsync();
        var asOf = request.OpeningAsOf ?? today;
        if (MasterRules.CheckCreateParty(member, type, request.OpeningBalance, asOf, today) is { } denied)
        {
            return WriteResult.Forbidden(denied);
        }

        if (request.Id is { } requestedId && await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == requestedId) is { } existing)
        {
            return existing.CompanyId == companyId && existing.Type == AccountType.Party && !existing.IsDeleted
                ? new WriteResult(WriteStatus.Replayed, existing)
                : WriteResult.Conflict("That id is already in use.");
        }

        if (request.CategoryId is { } catId && !await db.PartyCategories.AnyAsync(c => c.Id == catId && c.CompanyId == companyId && !c.IsDeleted))
        {
            return WriteResult.Invalid("categoryId", "That party category doesn't exist.");
        }

        var party = new Account
        {
            Id = request.Id ?? Guid.CreateVersion7(),
            CompanyId = companyId,
            Name = request.Name.Trim(),
            Type = AccountType.Party,
            PartyType = type,
            Phone = MasterValidation.Clean(request.Phone),
            Email = MasterValidation.Clean(request.Email),
            Address = MasterValidation.Clean(request.Address),
            Pan = MasterValidation.Clean(request.Pan),
            PartyCategoryId = request.CategoryId,
            OpeningBalance = request.OpeningBalance,
            OpeningSide = MasterValidation.ParseOpeningSide(request.OpeningSide)!.Value,
            OpeningAsOf = asOf,
        };
        db.Accounts.Add(party);
        AuditAccount(party, "Create", before: null);
        await SaveAsync(party);
        return new WriteResult(WriteStatus.Created, party);
    }

    public async Task<WriteResult> UpdatePartyAsync(Guid partyId, SavePartyRequest request, long? expectedVersion)
    {
        var party = await db.Accounts.SingleOrDefaultAsync(a => a.Id == partyId && a.CompanyId == companyId && a.Type == AccountType.Party && !a.IsDeleted);
        if (party is null)
        {
            return new WriteResult(WriteStatus.NotFound, Error: "That party doesn't exist.");
        }

        if (MasterValidation.Validate(request) is { Count: > 0 } errors)
        {
            return WriteResult.Invalid(errors);
        }

        var newType = MasterValidation.ParsePartyType(request.PartyType)!.Value;
        var side = MasterValidation.ParseOpeningSide(request.OpeningSide)!.Value;
        var (_, today) = await TodayAsync();
        var amountChanged = party.OpeningBalance != request.OpeningBalance || party.OpeningSide != side;
        var asOf = request.OpeningAsOf ?? (amountChanged ? today : party.OpeningAsOf ?? today);
        var openingChanged = amountChanged || party.OpeningAsOf != asOf;
        if (MasterRules.CheckEditParty(member, party.PartyType!.Value, newType, openingChanged, request.OpeningBalance, asOf, today) is { } denied)
        {
            return WriteResult.Forbidden(denied);
        }

        // D8 / AGENT_PLAN § 6.3: opening balance fields are money, so concurrent edits go to conflict review.
        // Non-money master fields use last-writer-wins with the previous value recorded in AuditLog.
        var basedOn = party.Version;
        if (expectedVersion is { } expected && expected != basedOn && openingChanged)
        {
            return WriteResult.Conflict("This party's opening balance was changed elsewhere since you opened it.");
        }

        if (request.CategoryId is { } catId && !await db.PartyCategories.AnyAsync(c => c.Id == catId && c.CompanyId == companyId && !c.IsDeleted))
        {
            return WriteResult.Invalid("categoryId", "That party category doesn't exist.");
        }

        var before = SnapshotAccount(party);
        party.Name = request.Name.Trim();
        party.PartyType = newType;
        party.Phone = MasterValidation.Clean(request.Phone);
        party.Email = MasterValidation.Clean(request.Email);
        party.Address = MasterValidation.Clean(request.Address);
        party.Pan = MasterValidation.Clean(request.Pan);
        party.PartyCategoryId = request.CategoryId;
        party.OpeningBalance = request.OpeningBalance;
        party.OpeningSide = side;
        party.OpeningAsOf = asOf;
        AuditAccount(party, expectedVersion is not null && expectedVersion != basedOn ? "ConflictLost" : "Update", before);
        await SaveAsync(party);
        return new WriteResult(WriteStatus.Saved, party);
    }

    public async Task<WriteResult> DeletePartyAsync(Guid partyId, long? expectedVersion)
    {
        var party = await db.Accounts.SingleOrDefaultAsync(a => a.Id == partyId && a.CompanyId == companyId && a.Type == AccountType.Party);
        if (party is null)
        {
            return new WriteResult(WriteStatus.NotFound, Error: "That party doesn't exist.");
        }

        if (MasterRules.CheckDeleteParty(member, party.PartyType!.Value) is { } denied)
        {
            return WriteResult.Forbidden(denied);
        }

        if (party.IsDeleted)
        {
            return new WriteResult(WriteStatus.Replayed, party);
        }

        if (expectedVersion is { } expected && expected != party.Version)
        {
            return WriteResult.Conflict("This party was changed elsewhere before deletion.");
        }

        if (await db.Transactions.AnyAsync(t => t.CompanyId == companyId && !t.IsDeleted && (t.FromAccountId == partyId || t.ToAccountId == partyId)))
        {
            return WriteResult.Conflict(MasterRules.PartyInUse);
        }

        var before = SnapshotAccount(party);
        party.IsDeleted = true;
        AuditAccount(party, "Delete", before);
        await SaveAsync(party);
        return new WriteResult(WriteStatus.Saved, party);
    }

    // ---------------- Money accounts (for sync push, same rules as MasterEndpoints) ----------------

    public async Task<WriteResult> CreateMoneyAccountAsync(SaveMoneyAccountRequest request)
    {
        if (!member.Has(Permission.AccountsAdd))
        {
            return WriteResult.Forbidden("You don't have permission to add accounts.");
        }

        if (MasterValidation.Validate(request, isNew: true) is { Count: > 0 } errors)
        {
            return WriteResult.Invalid(errors);
        }

        var (_, today) = await TodayAsync();
        var asOf = request.OpeningAsOf ?? today;
        if (MasterRules.CheckMoneyAccountOpening(member, request.OpeningBalance, asOf, today) is { } denied)
        {
            return WriteResult.Forbidden(denied);
        }

        if (request.Id is { } requestedId && await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == requestedId) is { } existing)
        {
            return existing.CompanyId == companyId && existing.IsMoneyAccount && !existing.IsDeleted
                ? new WriteResult(WriteStatus.Replayed, existing)
                : WriteResult.Conflict("That id is already in use.");
        }

        var type = MasterValidation.ParseMoneyType(request.Type)!.Value;
        var hasBankDetails = type is AccountType.Bank or AccountType.Wallet;
        var account = new Account
        {
            Id = request.Id ?? Guid.CreateVersion7(),
            CompanyId = companyId,
            Name = request.Name.Trim(),
            Type = type,
            AccountHolderName = hasBankDetails ? MasterValidation.Clean(request.AccountHolderName) : null,
            AccountNumber = hasBankDetails ? MasterValidation.Clean(request.AccountNumber) : null,
            OpeningBalance = Math.Abs(request.OpeningBalance),
            OpeningSide = request.OpeningBalance < 0 ? BalanceSide.Credit : BalanceSide.Debit,
            OpeningAsOf = asOf,
        };
        db.Accounts.Add(account);
        AuditAccount(account, "Create", before: null);
        await SaveAsync(account);
        return new WriteResult(WriteStatus.Created, account);
    }

    public async Task<WriteResult> UpdateMoneyAccountAsync(Guid accountId, SaveMoneyAccountRequest request, long? expectedVersion)
    {
        if (!member.Has(Permission.AccountsEdit))
        {
            return WriteResult.Forbidden("You don't have permission to edit accounts.");
        }

        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == accountId && a.CompanyId == companyId && !a.IsDeleted
            && (a.Type == AccountType.Cash || a.Type == AccountType.Bank || a.Type == AccountType.Cheque || a.Type == AccountType.Wallet));
        if (account is null)
        {
            return new WriteResult(WriteStatus.NotFound, Error: "That account doesn't exist.");
        }

        if (MasterValidation.Validate(request, isNew: false) is { Count: > 0 } errors)
        {
            return WriteResult.Invalid(errors);
        }

        var type = MasterValidation.ParseMoneyType(request.Type)!.Value;
        if (type != account.Type && (account.IsSystem || !MasterValidation.AddableMoneyTypes.Contains(type)))
        {
            return WriteResult.Invalid("type", "This account's type can't be changed.");
        }

        var (_, today) = await TodayAsync();
        var amountChanged = Balances.SignedOpening(account) != request.OpeningBalance;
        var asOf = request.OpeningAsOf ?? (amountChanged ? today : account.OpeningAsOf ?? today);
        var openingChanged = amountChanged || account.OpeningAsOf != asOf;
        if (openingChanged && MasterRules.CheckMoneyAccountOpening(member, request.OpeningBalance, asOf, today) is { } denied)
        {
            return WriteResult.Forbidden(denied);
        }

        var basedOn = account.Version;
        if (expectedVersion is { } expected && expected != basedOn && openingChanged)
        {
            return WriteResult.Conflict("This account's opening balance was changed elsewhere since you opened it.");
        }

        var before = SnapshotAccount(account);
        var hasBankDetails = type is AccountType.Bank or AccountType.Wallet;
        account.Name = request.Name.Trim();
        account.Type = type;
        account.AccountHolderName = hasBankDetails ? MasterValidation.Clean(request.AccountHolderName) : null;
        account.AccountNumber = hasBankDetails ? MasterValidation.Clean(request.AccountNumber) : null;
        account.OpeningBalance = Math.Abs(request.OpeningBalance);
        account.OpeningSide = request.OpeningBalance < 0 ? BalanceSide.Credit : BalanceSide.Debit;
        account.OpeningAsOf = asOf;
        AuditAccount(account, expectedVersion is not null && expectedVersion != basedOn ? "ConflictLost" : "Update", before);
        await SaveAsync(account);
        return new WriteResult(WriteStatus.Saved, account);
    }

    public async Task<WriteResult> DeleteMoneyAccountAsync(Guid accountId, long? expectedVersion)
    {
        if (!member.Has(Permission.AccountsDelete))
        {
            return WriteResult.Forbidden("You don't have permission to delete accounts.");
        }

        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == accountId && a.CompanyId == companyId
            && (a.Type == AccountType.Cash || a.Type == AccountType.Bank || a.Type == AccountType.Cheque || a.Type == AccountType.Wallet));
        if (account is null)
        {
            return new WriteResult(WriteStatus.NotFound, Error: "That account doesn't exist.");
        }

        if (account.IsDeleted)
        {
            return new WriteResult(WriteStatus.Replayed, account);
        }

        if (expectedVersion is { } expected && expected != account.Version)
        {
            return WriteResult.Conflict("This account was changed elsewhere before deletion.");
        }

        var inUse = await db.Transactions.AnyAsync(t => t.CompanyId == companyId && !t.IsDeleted && (t.FromAccountId == accountId || t.ToAccountId == accountId));
        if (MasterRules.CheckDeleteMoneyAccount(account, inUse) is { } blocked)
        {
            return WriteResult.Conflict(blocked);
        }

        var before = SnapshotAccount(account);
        account.IsDeleted = true;
        AuditAccount(account, "Delete", before);
        await SaveAsync(account);
        return new WriteResult(WriteStatus.Saved, account);
    }

    // ---------------- Income / Expense heads & Adjustments ----------------

    public async Task<WriteResult> CreateHeadCategoryAsync(SaveCategoryRequest request)
    {
        if (!member.Has(Permission.SettingsFeatures))
        {
            return WriteResult.Forbidden("You don't have permission to add categories.");
        }

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 100)
        {
            return WriteResult.Invalid("name", "Enter a name of up to 100 characters.");
        }

        AccountType? type = request.Type switch
        {
            nameof(AccountType.Expense) => AccountType.Expense,
            nameof(AccountType.Income) => AccountType.Income,
            _ => null,
        };
        if (type is not { } headType)
        {
            return WriteResult.Invalid("type", "Choose Expense or Income.");
        }

        if (request.Id == Guid.Empty)
        {
            return WriteResult.Invalid("id", "A new category needs an id.");
        }

        if (await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == request.Id) is { } existing)
        {
            return existing.CompanyId == companyId && existing.Type == headType && !existing.IsDeleted
                ? new WriteResult(WriteStatus.Replayed, existing)
                : WriteResult.Conflict("That id is already in use.");
        }

        var names = await db.Accounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.Type == headType)
            .Select(a => a.Name)
            .ToListAsync();
        if (names.Contains(name, StringComparer.CurrentCultureIgnoreCase))
        {
            return WriteResult.Conflict("There is already a category with that name.");
        }

        var account = new Account { Id = request.Id, CompanyId = companyId, Name = name, Type = headType };
        db.Accounts.Add(account);
        AuditAccount(account, "Create", before: null);
        await SaveAsync(account);
        return new WriteResult(WriteStatus.Created, account);
    }

    public async Task<WriteResult> CreateAdjustmentAsync(Guid accountId, AdjustBalanceRequest request)
    {
        var account = await db.Accounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == accountId && a.CompanyId == companyId && !a.IsDeleted);
        if (account is null || !(account.Type == AccountType.Party || account.IsMoneyAccount))
        {
            return new WriteResult(WriteStatus.NotFound, Error: "That account doesn't exist.");
        }

        var view = account.Type == AccountType.Party
            ? MasterRules.PartyViewPermission(account.PartyType ?? PartyType.Customer)
            : Permission.AccountsView;
        if (!member.Has(view))
        {
            return WriteResult.Forbidden("You don't have permission to view this account.");
        }

        var system = await db.Accounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.Type == AccountType.System)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync();
        if (system is not { } adjustment)
        {
            return WriteResult.Conflict("This company has no adjustment account.");
        }

        var type = account.Type == AccountType.Party
            ? request.Increase ? TransactionType.PartyAdjustAdd : TransactionType.PartyAdjustReduce
            : request.Increase ? TransactionType.AddMoney : TransactionType.ReduceMoney;
        var (from, to) = request.Increase ? (adjustment, account.Id) : (account.Id, adjustment);

        return await CreateTransactionAsync(new CreateTransactionRequest(
            type.ToString(), request.Date, request.Amount, from, to, request.Remarks, null, null, request.Id));
    }

    public async Task<WriteResult> DeleteAttachmentAsync(Guid attachmentId)
    {
        var attachment = await db.Attachments.SingleOrDefaultAsync(a => a.Id == attachmentId && a.CompanyId == companyId);
        if (attachment is null)
        {
            return new WriteResult(WriteStatus.NotFound, Error: "That attachment doesn't exist.");
        }

        if (attachment.IsDeleted)
        {
            return new WriteResult(WriteStatus.Replayed, attachment);
        }

        if (attachment.TransactionId is not { } txnId || await FindTransactionAsync(txnId) is not { } txn)
        {
            return new WriteResult(WriteStatus.NotFound, Error: "That entry doesn't exist.");
        }

        var (zone, today) = await TodayAsync();
        if (EntryRules.CheckChange(member, txn, EntryAction.Edit, null, today, zone) is { } denied)
        {
            return WriteResult.Forbidden(denied);
        }

        attachment.IsDeleted = true;
        await SaveAsync(attachment);
        return new WriteResult(WriteStatus.Saved, attachment);
    }

    // ---------------- Helpers ----------------

    private async Task<WriteResult?> SaveTransactionAsync(Transaction transaction, bool checkDuplicates, long? version = null)
    {
        var party = checkDuplicates ? PartyId(transaction.Type, transaction.FromAccountId, transaction.ToAccountId) : null;
        if (party is not { } partyId)
        {
            return await SaveAsync(transaction, version) ? null : WriteResult.Conflict(ChangedElsewhere);
        }

        var visible = Enum.GetValues<TransactionType>()
            .Where(t => PartyId(t, Guid.Empty, Guid.Empty) is not null && member.Has(EntryRules.ViewPermissionFor(t)))
            .ToArray();
        WriteResult? blocked = null;
        await db.SaveWithVersionsAsync(companyId, [transaction], member.UserId, clock.GetUtcNow().UtcDateTime, async () =>
        {
            if (version is { } expected && !await IsStillAtAsync(transaction.Id, expected))
            {
                blocked = WriteResult.Conflict(ChangedElsewhere);
                return false;
            }

            // This runs under the company version lock, so a concurrent save can't pass the same check.
            var matches = await db.Transactions.AsNoTracking()
                .Where(t => t.CompanyId == companyId && t.Id != transaction.Id && !t.IsDeleted
                    && t.Status == TransactionStatus.Posted && t.Date == transaction.Date && t.Amount == transaction.Amount
                    && (t.FromAccountId == partyId || t.ToAccountId == partyId) && visible.Contains(t.Type))
                .OrderByDescending(t => t.CreatedAtUtc).ThenBy(t => t.Id)
                .ToListAsync();
            if (matches.Count == 0)
            {
                return true;
            }

            blocked = new WriteResult(WriteStatus.PossibleDuplicate, Duplicates: matches);
            return false;
        });
        return blocked;
    }

    private static Guid? PartyId(TransactionType type, Guid from, Guid to) => PostingRules.RolesFor(type) switch
    {
        (PostingRules.AccountRole.Party, _) => from,
        (_, PostingRules.AccountRole.Party) => to,
        _ => null,
    };

    private async Task<bool> SaveAsync(SyncEntity row, long? onlyIfStillAt = null) =>
        await db.SaveWithVersionsAsync(
            companyId, [row], member.UserId, clock.GetUtcNow().UtcDateTime,
            onlyIfStillAt is { } version
                ? () => IsStillAtAsync(row.Id, version)
                : null);

    private Task<bool> IsStillAtAsync(Guid id, long version) =>
        db.Transactions.AsNoTracking().AnyAsync(t => t.Id == id && t.Version == version);

    private Task<Transaction?> FindTransactionAsync(Guid id) =>
        db.Transactions.SingleOrDefaultAsync(t => t.Id == id && t.CompanyId == companyId && !t.IsDeleted);

    /// <summary>By id across companies: only to tell a retry from an id that is already taken.</summary>
    private Task<Transaction?> FindTransactionByIdAsync(Guid id) =>
        db.Transactions.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id);

    private WriteResult Replay(Transaction existing) =>
        existing.CompanyId != companyId || existing.IsDeleted || existing.CreatedBy != member.UserId
            ? WriteResult.Conflict("That id is already in use.")
            : new WriteResult(WriteStatus.Replayed, existing);

    private static bool Matches(Transaction t, UpdateTransactionRequest r) =>
        t.Date == r.Date && t.Amount == r.Amount && t.FromAccountId == r.FromAccountId && t.ToAccountId == r.ToAccountId
        && t.Remarks == MasterValidation.Clean(r.Remarks) && t.ChequeNo == MasterValidation.Clean(r.ChequeNo)
        && t.ChequeDate == r.ChequeDate;

    /// <summary>
    /// Loads both accounts <em>from this company only</em> and applies the posting rules, so an unknown or
    /// another company's account is a bad request.
    /// </summary>
    private async Task<WriteResult?> PostingProblemAsync(Transaction transaction)
    {
        var ids = new[] { transaction.FromAccountId, transaction.ToAccountId };
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && ids.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id);

        if (!accounts.TryGetValue(transaction.FromAccountId, out var from))
        {
            return WriteResult.Invalid("fromAccountId", "That account doesn't exist.");
        }

        if (!accounts.TryGetValue(transaction.ToAccountId, out var to))
        {
            return WriteResult.Invalid("toAccountId", "That account doesn't exist.");
        }

        var errors = PostingRules.Validate(transaction, from, to);
        return errors.Count == 0 ? null : WriteResult.Invalid(new Dictionary<string, string[]> { ["transaction"] = [.. errors] });
    }

    private async Task<(string Zone, DateOnly Today)> TodayAsync()
    {
        var zone = await db.Companies.Where(c => c.Id == companyId).Select(c => c.TimeZoneId).SingleAsync();
        return (zone, CompanyClock.Today(zone, clock));
    }

    private static string Snapshot(Transaction t) => JsonSerializer.Serialize(new
    {
        Type = t.Type.ToString(), t.VoucherNo, t.Date, t.Amount, t.FromAccountId, t.ToAccountId,
        t.Remarks, t.ChequeNo, t.ChequeDate, Status = t.Status.ToString(),
    });

    private void Audit(Transaction transaction, string action, string? before) =>
        db.AuditLogs.Add(new AuditLog
        {
            CompanyId = companyId,
            EntityType = nameof(Transaction),
            EntityId = transaction.Id,
            Action = action,
            BeforeJson = before,
            AfterJson = Snapshot(transaction),
            UserId = member.UserId,
            AtUtc = clock.GetUtcNow().UtcDateTime,
        });

    private static string SnapshotAccount(Account a) => JsonSerializer.Serialize(new
    {
        a.Name, Type = a.Type.ToString(), PartyType = a.PartyType?.ToString(), a.Phone, a.Email, a.Address, a.Pan,
        a.PartyCategoryId, a.OpeningBalance, OpeningSide = a.OpeningSide.ToString(), a.OpeningAsOf,
        a.AccountHolderName, a.AccountNumber, a.IsDeleted,
    });

    private void AuditAccount(Account account, string action, string? before) =>
        db.AuditLogs.Add(new AuditLog
        {
            CompanyId = companyId,
            EntityType = nameof(Account),
            EntityId = account.Id,
            Action = action,
            BeforeJson = before,
            AfterJson = action == "Delete" ? null : SnapshotAccount(account),
            UserId = member.UserId,
            AtUtc = clock.GetUtcNow().UtcDateTime,
        });
}
