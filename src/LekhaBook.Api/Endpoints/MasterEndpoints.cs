using System.Text.Json;
using LekhaBook.Api.Security;
using LekhaBook.Application;
using LekhaBook.Contracts;
using LekhaBook.Data;
using LekhaBook.Data.Postgres;
using LekhaBook.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace LekhaBook.Api.Endpoints;

/// <summary>
/// Parties, money accounts and party categories (AGENT_PLAN § 5.1, D14). Every lookup is filtered by the
/// route's company as well as the id, because the access policy only proves membership. Every write
/// is stamped with a server version and audited.
/// </summary>
public static class MasterEndpoints
{
    public static void MapMasterEndpoints(this IEndpointRouteBuilder app)
    {
        var company = app.MapGroup("/api/companies/{companyId:guid}").RequireAuthorization().RequireCompanyMember();

        // Party permissions depend on the party's type (customer/supplier), so the handlers check them.
        company.MapGet("/parties", ListParties);
        company.MapGet("/parties/{id:guid}", GetParty);
        company.MapPost("/parties", CreateParty);
        company.MapPut("/parties/{id:guid}", UpdateParty);
        company.MapDelete("/parties/{id:guid}", DeleteParty);
        company.MapGet("/parties/{id:guid}/ledger", PartyLedger);

        company.MapGet("/party-categories", ListCategories);
        company.MapPost("/party-categories", CreateCategory).RequirePermission(Permission.SettingsFeatures);

        company.MapGet("/accounts", ListAccounts).RequirePermission(Permission.AccountsView);
        company.MapGet("/accounts/{id:guid}", GetAccount).RequirePermission(Permission.AccountsView);
        company.MapPost("/accounts", CreateAccount).RequirePermission(Permission.AccountsAdd);
        company.MapPut("/accounts/{id:guid}", UpdateAccount).RequirePermission(Permission.AccountsEdit);
        company.MapDelete("/accounts/{id:guid}", DeleteAccount).RequirePermission(Permission.AccountsDelete);
        company.MapGet("/accounts/{id:guid}/ledger", AccountLedger).RequirePermission(Permission.AccountsView);
    }

    // ---------------- Parties ----------------

    private static async Task<Results<Ok<List<PartyDto>>, ProblemHttpResult>> ListParties(
        Guid companyId, CompanyAccess access, PostgresDbContext db)
    {
        var member = await access.RequireMemberAsync();
        var visible = Enum.GetValues<PartyType>()
            .Where(t => member.Has(MasterRules.PartyViewPermission(t)))
            .Select(t => (PartyType?)t)
            .ToList();
        if (visible.Count == 0)
        {
            return Forbidden("You don't have permission to view parties.");
        }

        var parties = await db.Accounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.Type == AccountType.Party && visible.Contains(a.PartyType))
            .OrderBy(a => a.Name)
            .ToListAsync();
        var balances = await db.GetBalancesAsync(companyId);
        var categories = await CategoryNamesAsync(db, companyId);

        return TypedResults.Ok(parties.Select(p => ToDto(p, balances.GetValueOrDefault(p.Id), categories)).ToList());
    }

    private static async Task<Results<Ok<PartyDto>, NotFound, ProblemHttpResult>> GetParty(
        Guid companyId, Guid id, CompanyAccess access, PostgresDbContext db)
    {
        var member = await access.RequireMemberAsync();
        var party = await FindPartyAsync(db, companyId, id, tracked: false);
        if (party is null)
        {
            return TypedResults.NotFound();
        }

        if (!member.Has(MasterRules.PartyViewPermission(party.PartyType!.Value)))
        {
            return Forbidden("You don't have permission to view this party.");
        }

        return TypedResults.Ok(await ToDtoAsync(db, party));
    }

    private static async Task<Results<Created<PartyDto>, Ok<PartyDto>, ValidationProblem, ProblemHttpResult>> CreateParty(
        Guid companyId, SavePartyRequest request, CompanyAccess access, PostgresDbContext db, TimeProvider clock)
    {
        var member = await access.RequireMemberAsync();
        if (MasterValidation.Validate(request) is { Count: > 0 } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var type = MasterValidation.ParsePartyType(request.PartyType)!.Value;
        var today = await CompanyTodayAsync(db, companyId, clock);
        var asOf = request.OpeningAsOf ?? today;
        if (MasterRules.CheckCreateParty(member, type, request.OpeningBalance, asOf, today) is { } denied)
        {
            return Forbidden(denied);
        }

        // A retried create (same client-generated id) returns what the first attempt saved.
        if (request.Id is { } requestedId && await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == requestedId) is { } existing)
        {
            return existing.CompanyId == companyId && existing.Type == AccountType.Party && !existing.IsDeleted
                ? TypedResults.Ok(await ToDtoAsync(db, existing))
                : Conflict("That id is already in use.");
        }

        if (await CategoryProblemAsync(db, companyId, request.CategoryId) is { } categoryProblem)
        {
            return categoryProblem;
        }

        var party = new Account
        {
            Id = request.Id ?? Guid.CreateVersion7(),
            CompanyId = companyId,
            Name = request.Name.Trim(),
            Type = AccountType.Party,
        };
        Apply(party, request, type, asOf);
        db.Accounts.Add(party);
        Audit(db, member, party, "Create", before: null, clock);
        await db.SaveWithVersionsAsync(companyId, [party], member.UserId, clock.GetUtcNow().UtcDateTime);

        return TypedResults.Created($"/api/companies/{companyId}/parties/{party.Id}", await ToDtoAsync(db, party));
    }

    private static async Task<Results<Ok<PartyDto>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateParty(
        Guid companyId, Guid id, SavePartyRequest request, CompanyAccess access, PostgresDbContext db, TimeProvider clock)
    {
        var member = await access.RequireMemberAsync();
        var party = await FindPartyAsync(db, companyId, id, tracked: true);
        if (party is null)
        {
            return TypedResults.NotFound();
        }

        if (MasterValidation.Validate(request) is { Count: > 0 } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var newType = MasterValidation.ParsePartyType(request.PartyType)!.Value;
        var side = MasterValidation.ParseOpeningSide(request.OpeningSide)!.Value;
        var today = await CompanyTodayAsync(db, companyId, clock);
        // No date sent (e.g. the form's date is locked): a changed amount is "as of today", otherwise keep the old date.
        var amountChanged = party.OpeningBalance != request.OpeningBalance || party.OpeningSide != side;
        var asOf = request.OpeningAsOf ?? (amountChanged ? today : party.OpeningAsOf ?? today);
        var openingChanged = amountChanged || party.OpeningAsOf != asOf;
        if (MasterRules.CheckEditParty(member, party.PartyType!.Value, newType, openingChanged, request.OpeningBalance, asOf, today) is { } denied)
        {
            return Forbidden(denied);
        }

        if (await CategoryProblemAsync(db, companyId, request.CategoryId) is { } categoryProblem)
        {
            return categoryProblem;
        }

        var before = Snapshot(party);
        party.Name = request.Name.Trim();
        Apply(party, request, newType, asOf);
        Audit(db, member, party, "Update", before, clock);
        await db.SaveWithVersionsAsync(companyId, [party], member.UserId, clock.GetUtcNow().UtcDateTime);

        return TypedResults.Ok(await ToDtoAsync(db, party));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteParty(
        Guid companyId, Guid id, CompanyAccess access, PostgresDbContext db, TimeProvider clock)
    {
        var member = await access.RequireMemberAsync();
        var party = await FindPartyAsync(db, companyId, id, tracked: true);
        if (party is null)
        {
            return TypedResults.NotFound();
        }

        if (MasterRules.CheckDeleteParty(member, party.PartyType!.Value) is { } denied)
        {
            return Forbidden(denied);
        }

        if (await db.IsUsedByTransactionsAsync(companyId, id))
        {
            return Conflict(MasterRules.PartyInUse);
        }

        var before = Snapshot(party);
        party.IsDeleted = true;
        Audit(db, member, party, "Delete", before, clock);
        await db.SaveWithVersionsAsync(companyId, [party], member.UserId, clock.GetUtcNow().UtcDateTime);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<LedgerDto>, NotFound, ProblemHttpResult>> PartyLedger(
        Guid companyId, Guid id, CompanyAccess access, PostgresDbContext db)
    {
        var member = await access.RequireMemberAsync();
        var ledger = await db.GetLedgerAsync(companyId, id);
        if (ledger is null || ledger.Account.Type != AccountType.Party)
        {
            return TypedResults.NotFound();
        }

        return member.Has(MasterRules.PartyViewPermission(ledger.Account.PartyType!.Value))
            ? TypedResults.Ok(ToDto(ledger))
            : Forbidden("You don't have permission to view this party.");
    }

    // ---------------- Party categories ----------------

    private static async Task<Ok<List<PartyCategoryDto>>> ListCategories(Guid companyId, PostgresDbContext db)
    {
        var categories = await db.PartyCategories.AsNoTracking()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted)
            .OrderBy(c => c.Name)
            .Select(c => new PartyCategoryDto(c.Id, c.Name))
            .ToListAsync();
        return TypedResults.Ok(categories);
    }

    private static async Task<Results<Created<PartyCategoryDto>, Ok<PartyCategoryDto>, ValidationProblem, ProblemHttpResult>> CreateCategory(
        Guid companyId, SavePartyCategoryRequest request, CompanyAccess access, PostgresDbContext db, TimeProvider clock)
    {
        var writer = new BooksWriter(db, clock, companyId, await access.RequireMemberAsync());
        var result = await writer.CreateCategoryAsync(request);
        if (result.Row is not PartyCategory category)
        {
            return result.Status switch
            {
                WriteStatus.Invalid => TypedResults.ValidationProblem(result.Errors!.ToDictionary()),
                WriteStatus.Forbidden => Forbidden(result.Error!),
                _ => Conflict(result.Error!),
            };
        }

        var dto = new PartyCategoryDto(category.Id, category.Name);
        return result.Status == WriteStatus.Created
            ? TypedResults.Created($"/api/companies/{companyId}/party-categories/{category.Id}", dto)
            : TypedResults.Ok(dto);
    }

    // ---------------- Money accounts ----------------

    private static async Task<Ok<List<MoneyAccountDto>>> ListAccounts(Guid companyId, PostgresDbContext db)
    {
        var accounts = await MoneyAccounts(db, companyId).AsNoTracking().ToListAsync();
        var balances = await db.GetBalancesAsync(companyId);
        return TypedResults.Ok(accounts
            .OrderBy(a => TypeOrder(a.Type)).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => ToDto(a, balances.GetValueOrDefault(a.Id)))
            .ToList());
    }

    private static async Task<Results<Ok<MoneyAccountDto>, NotFound>> GetAccount(Guid companyId, Guid id, PostgresDbContext db)
    {
        var account = await MoneyAccounts(db, companyId).AsNoTracking().SingleOrDefaultAsync(a => a.Id == id);
        return account is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(ToDto(account, await db.GetBalanceAsync(account)));
    }

    private static async Task<Results<Created<MoneyAccountDto>, Ok<MoneyAccountDto>, ValidationProblem, ProblemHttpResult>> CreateAccount(
        Guid companyId, SaveMoneyAccountRequest request, CompanyAccess access, PostgresDbContext db, TimeProvider clock)
    {
        var member = await access.RequireMemberAsync();
        if (MasterValidation.Validate(request, isNew: true) is { Count: > 0 } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var today = await CompanyTodayAsync(db, companyId, clock);
        var asOf = request.OpeningAsOf ?? today;
        if (MasterRules.CheckMoneyAccountOpening(member, request.OpeningBalance, asOf, today) is { } denied)
        {
            return Forbidden(denied);
        }

        if (request.Id is { } requestedId && await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == requestedId) is { } existing)
        {
            return existing.CompanyId == companyId && existing.IsMoneyAccount && !existing.IsDeleted
                ? TypedResults.Ok(ToDto(existing, await db.GetBalanceAsync(existing)))
                : Conflict("That id is already in use.");
        }

        var account = new Account
        {
            Id = request.Id ?? Guid.CreateVersion7(),
            CompanyId = companyId,
            Name = request.Name.Trim(),
            Type = MasterValidation.ParseMoneyType(request.Type)!.Value,
        };
        Apply(account, request, account.Type, asOf);
        db.Accounts.Add(account);
        Audit(db, member, account, "Create", before: null, clock);
        await db.SaveWithVersionsAsync(companyId, [account], member.UserId, clock.GetUtcNow().UtcDateTime);

        return TypedResults.Created($"/api/companies/{companyId}/accounts/{account.Id}", ToDto(account, Balances.SignedOpening(account)));
    }

    private static async Task<Results<Ok<MoneyAccountDto>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateAccount(
        Guid companyId, Guid id, SaveMoneyAccountRequest request, CompanyAccess access, PostgresDbContext db, TimeProvider clock)
    {
        var member = await access.RequireMemberAsync();
        var account = await MoneyAccounts(db, companyId).SingleOrDefaultAsync(a => a.Id == id);
        if (account is null)
        {
            return TypedResults.NotFound();
        }

        if (MasterValidation.Validate(request, isNew: false) is { Count: > 0 } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        // Built-in accounts (Cash, Cheque) keep their type; others can move between Bank, Wallet and Cash.
        var type = MasterValidation.ParseMoneyType(request.Type)!.Value;
        if (type != account.Type && (account.IsSystem || !MasterValidation.AddableMoneyTypes.Contains(type)))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["type"] = ["This account's type can't be changed."] });
        }

        var today = await CompanyTodayAsync(db, companyId, clock);
        // Same date rule as parties: no date sent + a changed amount means "as of today".
        var amountChanged = Balances.SignedOpening(account) != request.OpeningBalance;
        var asOf = request.OpeningAsOf ?? (amountChanged ? today : account.OpeningAsOf ?? today);
        var openingChanged = amountChanged || account.OpeningAsOf != asOf;
        if (openingChanged && MasterRules.CheckMoneyAccountOpening(member, request.OpeningBalance, asOf, today) is { } denied)
        {
            return Forbidden(denied);
        }

        var before = Snapshot(account);
        account.Name = request.Name.Trim();
        Apply(account, request, type, asOf);
        Audit(db, member, account, "Update", before, clock);
        await db.SaveWithVersionsAsync(companyId, [account], member.UserId, clock.GetUtcNow().UtcDateTime);

        return TypedResults.Ok(ToDto(account, await db.GetBalanceAsync(account)));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteAccount(
        Guid companyId, Guid id, CompanyAccess access, PostgresDbContext db, TimeProvider clock)
    {
        var member = await access.RequireMemberAsync();
        var account = await MoneyAccounts(db, companyId).SingleOrDefaultAsync(a => a.Id == id);
        if (account is null)
        {
            return TypedResults.NotFound();
        }

        if (MasterRules.CheckDeleteMoneyAccount(account, await db.IsUsedByTransactionsAsync(companyId, id)) is { } blocked)
        {
            return Conflict(blocked);
        }

        var before = Snapshot(account);
        account.IsDeleted = true;
        Audit(db, member, account, "Delete", before, clock);
        await db.SaveWithVersionsAsync(companyId, [account], member.UserId, clock.GetUtcNow().UtcDateTime);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<LedgerDto>, NotFound>> AccountLedger(Guid companyId, Guid id, PostgresDbContext db)
    {
        var ledger = await db.GetLedgerAsync(companyId, id);
        return ledger is null || !ledger.Account.IsMoneyAccount
            ? TypedResults.NotFound()
            : TypedResults.Ok(ToDto(ledger));
    }

    // ---------------- Helpers ----------------

    private static Task<Account?> FindPartyAsync(PostgresDbContext db, Guid companyId, Guid id, bool tracked)
    {
        var query = tracked ? db.Accounts : db.Accounts.AsNoTracking();
        return query.SingleOrDefaultAsync(a => a.Id == id && a.CompanyId == companyId && a.Type == AccountType.Party && !a.IsDeleted);
    }

    private static IQueryable<Account> MoneyAccounts(PostgresDbContext db, Guid companyId) =>
        db.Accounts.Where(a => a.CompanyId == companyId && !a.IsDeleted
            && (a.Type == AccountType.Cash || a.Type == AccountType.Bank || a.Type == AccountType.Cheque || a.Type == AccountType.Wallet));

    private static int TypeOrder(AccountType type) => type switch
    {
        AccountType.Cash => 0,
        AccountType.Bank => 1,
        AccountType.Wallet => 2,
        _ => 3,
    };

    private static void Apply(Account party, SavePartyRequest r, PartyType type, DateOnly asOf)
    {
        party.PartyType = type;
        party.Phone = MasterValidation.Clean(r.Phone);
        party.Email = MasterValidation.Clean(r.Email);
        party.Address = MasterValidation.Clean(r.Address);
        party.Pan = MasterValidation.Clean(r.Pan);
        party.PartyCategoryId = r.CategoryId;
        party.OpeningBalance = r.OpeningBalance;
        party.OpeningSide = MasterValidation.ParseOpeningSide(r.OpeningSide)!.Value;
        party.OpeningAsOf = asOf;
    }

    private static void Apply(Account account, SaveMoneyAccountRequest r, AccountType type, DateOnly asOf)
    {
        account.Type = type;
        var hasBankDetails = type is AccountType.Bank or AccountType.Wallet;
        account.AccountHolderName = hasBankDetails ? MasterValidation.Clean(r.AccountHolderName) : null;
        account.AccountNumber = hasBankDetails ? MasterValidation.Clean(r.AccountNumber) : null;

        // The form shows one signed "current balance"; negative means overdrawn.
        account.OpeningBalance = Math.Abs(r.OpeningBalance);
        account.OpeningSide = r.OpeningBalance < 0 ? BalanceSide.Credit : BalanceSide.Debit;
        account.OpeningAsOf = asOf;
    }

    private static async Task<DateOnly> CompanyTodayAsync(PostgresDbContext db, Guid companyId, TimeProvider clock)
    {
        var zone = await db.Companies.Where(c => c.Id == companyId).Select(c => c.TimeZoneId).SingleAsync();
        return CompanyClock.Today(zone, clock);
    }

    private static async Task<ValidationProblem?> CategoryProblemAsync(PostgresDbContext db, Guid companyId, Guid? categoryId) =>
        categoryId is { } id && !await db.PartyCategories.AnyAsync(c => c.Id == id && c.CompanyId == companyId && !c.IsDeleted)
            ? TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["categoryId"] = ["That party category doesn't exist."] })
            : null;

    private static Task<Dictionary<Guid, string>> CategoryNamesAsync(PostgresDbContext db, Guid companyId) =>
        db.PartyCategories.AsNoTracking()
            .Where(c => c.CompanyId == companyId)
            .ToDictionaryAsync(c => c.Id, c => c.Name);

    private static async Task<PartyDto> ToDtoAsync(PostgresDbContext db, Account party)
    {
        var categories = party.PartyCategoryId is { } categoryId
            ? await db.PartyCategories.AsNoTracking().Where(c => c.Id == categoryId).ToDictionaryAsync(c => c.Id, c => c.Name)
            : [];
        return ToDto(party, await db.GetBalanceAsync(party), categories);
    }

    private static PartyDto ToDto(Account p, decimal balance, IReadOnlyDictionary<Guid, string> categories) =>
        new(p.Id, p.Name, p.PartyType!.Value.ToString(), p.Phone, p.Email, p.Address, p.Pan,
            p.PartyCategoryId, p.PartyCategoryId is { } c ? categories.GetValueOrDefault(c) : null,
            p.OpeningBalance, MasterValidation.OpeningSideName(p.OpeningSide), p.OpeningAsOf, balance, p.CreatedAtUtc);

    private static MoneyAccountDto ToDto(Account a, decimal balance) =>
        new(a.Id, a.Name, a.Type.ToString(), a.AccountHolderName, a.AccountNumber,
            Balances.SignedOpening(a), a.OpeningAsOf, a.IsSystem, balance);

    private static LedgerDto ToDto(AccountLedger ledger) =>
        new(ledger.Account.Id, Balances.SignedOpening(ledger.Account), ledger.Account.OpeningAsOf,
            ledger.Lines.Select(l => new LedgerLineDto(
                l.Transaction.Id, l.Transaction.Type.ToString(), l.Transaction.VoucherNo, l.Transaction.Date,
                l.Debit, l.Credit, l.Balance, ledger.OtherSide(l.Transaction), l.Transaction.Remarks)).ToList(),
            ledger.ClosingBalance);

    /// <summary>The user-editable fields, for the audit trail.</summary>
    private static string Snapshot(Account a) => JsonSerializer.Serialize(new
    {
        a.Name, Type = a.Type.ToString(), PartyType = a.PartyType?.ToString(), a.Phone, a.Email, a.Address, a.Pan,
        a.PartyCategoryId, a.OpeningBalance, OpeningSide = a.OpeningSide.ToString(), a.OpeningAsOf,
        a.AccountHolderName, a.AccountNumber, a.IsDeleted,
    });

    private static void Audit(PostgresDbContext db, CompanyMember member, Account account, string action, string? before, TimeProvider clock) =>
        db.AuditLogs.Add(new AuditLog
        {
            CompanyId = account.CompanyId,
            EntityType = nameof(Account),
            EntityId = account.Id,
            Action = action,
            BeforeJson = before,
            AfterJson = action == "Delete" ? null : Snapshot(account),
            UserId = member.UserId,
            AtUtc = clock.GetUtcNow().UtcDateTime,
        });

    private static ProblemHttpResult Forbidden(string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status403Forbidden);

    private static ProblemHttpResult Conflict(string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status409Conflict);
}
