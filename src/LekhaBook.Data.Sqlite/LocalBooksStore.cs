using System.Text.Json;
using System.Text.Json.Serialization;
using LekhaBook.Application;
using LekhaBook.Contracts;
using LekhaBook.Data;
using LekhaBook.Domain;
using Microsoft.EntityFrameworkCore;

namespace LekhaBook.Data.Sqlite;

/// <summary>Outcome of a local SQLite operation, mirroring API semantics (status code + optional error / duplicate warning).</summary>
public sealed record LocalResult<T>(T? Value, string? Error, int StatusCode, DuplicateTransactionWarning? DuplicateWarning = null)
{
    public bool Ok => Error is null;

    public static LocalResult<T> Success(T value, int statusCode = 200) => new(value, null, statusCode);
    public static LocalResult<T> BadRequest(string error) => new(default, error, 400);
    public static LocalResult<T> Forbidden(string error) => new(default, error, 403);
    public static LocalResult<T> NotFound(string error = "Not found.") => new(default, error, 404);
    public static LocalResult<T> Conflict(string error) => new(default, error, 409);
    public static LocalResult<T> Duplicate(DuplicateTransactionWarning warning) =>
        new(default, "Possible duplicate entry.", 409, warning);
}

public sealed record LocalSyncIssue(long Id, Guid CompanyId, string EntityType, Guid EntityId, string Operation, int Attempts, string Error, DateTime CreatedAtUtc);

public sealed record LocalDataSummary(int Companies, int Parties, int MoneyAccounts, int Transactions, int Attachments);

public sealed record LocalAttachmentToUpload(Guid AttachmentId, Guid TransactionId, string FileName, string ContentType, string LocalPath);

public sealed record LocalCompanyMigrationBundle(
    MigrateOfflineCompanyRequest Snapshot,
    string? LogoPath,
    string? LogoContentType,
    IReadOnlyList<LocalAttachmentToUpload> Attachments);

/// <summary>
/// Full offline-first SQLite books engine shared by MAUI and tests.
/// Supports both standalone Offline Local Mode (enqueueOutbox = false) and Online-Synced Mode
/// (enqueueOutbox = true, writing entities and outbox items atomically with smart outbox coalescing).
/// </summary>
public sealed class LocalBooksStore(
    IDbContextFactory<SqliteDbContext> dbFactory,
    string filesRoot,
    TimeProvider? clock = null)
{
    private const long MaxAttachmentBytes = 5 * 1024 * 1024;
    private const long MaxLogoBytes = 2 * 1024 * 1024;
    private const int MaxAttachmentsPerEntry = 5;

    private static readonly string[] AllowedAttachmentTypes = ["image/jpeg", "image/png", "image/webp", "application/pdf"];
    private static readonly string[] AllowedLogoTypes = ["image/jpeg", "image/png", "image/webp"];

    public static readonly JsonSerializerOptions SyncJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private DateTime UtcNow => _clock.GetUtcNow().UtcDateTime;

    private readonly SemaphoreSlim _queueGate = new(1, 1);

    /// <summary>
    /// Held while the upload queue and the rows it describes are read and rewritten: by every change made here,
    /// and by sync while it claims items, records their results and applies a download. Without it an edit can
    /// slip between the engine reading an item and removing it, and be lost.
    /// </summary>
    public async Task<IDisposable> LockQueueAsync(CancellationToken ct = default)
    {
        await _queueGate.WaitAsync(ct);
        return new Held(_queueGate);
    }

    private sealed class Held(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    /// <summary>
    /// False once an item has been sent and no answer is recorded: the server may already hold it, so it can no
    /// longer be rewritten or removed. A later change is queued behind it instead.
    /// </summary>
    private static bool CanRewrite(OutboxItem item) => item.Attempts == 0 || item.LastError is not null;

    // ---------------- Companies (Local Mode & cached metadata) ----------------

    public async Task<List<CompanySummary>> ListCompaniesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var companies = await db.Companies.AsNoTracking()
            .Where(c => !c.IsDeleted)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
        var perms = RoleTemplates.For(CompanyRole.Owner).Order(StringComparer.Ordinal).ToList();
        return companies.Select(c => new CompanySummary(c.Id, c.Name, CompanyRole.Owner.ToString(), perms, c.Calendar.ToString())).ToList();
    }

    public async Task<LocalResult<CompanySummary>> CreateLocalCompanyAsync(CreateCompanyRequest request, Guid userId, CancellationToken ct = default)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 200)
        {
            return LocalResult<CompanySummary>.BadRequest("Business name is required (at most 200 characters).");
        }

        var calendar = CalendarMode.BS;
        if (!string.IsNullOrWhiteSpace(request.Calendar))
        {
            if (!Enum.TryParse<CalendarMode>(request.Calendar, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return LocalResult<CompanySummary>.BadRequest("Calendar must be BS or AD.");
            }

            calendar = parsed;
        }

        var now = UtcNow;
        var company = new Company
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Phone = MasterValidation.Clean(request.Phone),
            Address = MasterValidation.Clean(request.Address),
            Pan = MasterValidation.Clean(request.Pan),
            Calendar = calendar,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var accounts = CompanySeeder.DefaultAccounts(company.Id, now, userId);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Companies.Add(company);
        db.Accounts.AddRange(accounts);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var perms = RoleTemplates.For(CompanyRole.Owner).Order(StringComparer.Ordinal).ToList();
        return LocalResult<CompanySummary>.Success(new CompanySummary(company.Id, company.Name, CompanyRole.Owner.ToString(), perms, company.Calendar.ToString()), 201);
    }

    public async Task EnsureCompanyMetadataAsync(CompanyDetails details, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == details.CompanyId, ct);
        var cal = Enum.TryParse<CalendarMode>(details.Calendar, true, out var parsed) ? parsed : CalendarMode.BS;
        if (company is null)
        {
            db.Companies.Add(new Company
            {
                Id = details.CompanyId,
                Name = details.Name,
                Phone = details.Phone,
                Email = details.Email,
                Address = details.Address,
                Pan = details.Pan,
                Calendar = cal,
                Currency = details.Currency,
                TimeZoneId = details.TimeZoneId,
                FiscalYearStartMonth = details.FiscalYearStartMonth,
                CreatedAtUtc = UtcNow,
                UpdatedAtUtc = UtcNow,
            });
        }
        else
        {
            company.Name = details.Name;
            company.Phone = details.Phone;
            company.Email = details.Email;
            company.Address = details.Address;
            company.Pan = details.Pan;
            company.Calendar = cal;
            company.Currency = details.Currency;
            company.TimeZoneId = details.TimeZoneId;
            company.FiscalYearStartMonth = details.FiscalYearStartMonth;
            company.UpdatedAtUtc = UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<LocalResult<CompanyDetails>> GetCompanyDetailsAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var c = await db.Companies.AsNoTracking().SingleOrDefaultAsync(x => x.Id == companyId && !x.IsDeleted, ct);
        if (c is null)
        {
            return LocalResult<CompanyDetails>.NotFound();
        }

        return LocalResult<CompanyDetails>.Success(new CompanyDetails(
            c.Id, c.Name, c.Phone, c.Email, c.Address, c.Pan,
            c.Calendar.ToString(), c.Currency, c.TimeZoneId, c.FiscalYearStartMonth, HasLogo: c.LogoStorageKey != null));
    }

    public async Task<LocalResult<bool>> UpdateCalendarAsync(Guid companyId, string calendar, CancellationToken ct = default)
    {
        if (!Enum.TryParse<CalendarMode>(calendar, ignoreCase: true, out var mode) || !Enum.IsDefined(mode))
        {
            return LocalResult<bool>.BadRequest("Calendar must be BS or AD.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == companyId && !c.IsDeleted, ct);
        if (company is null)
        {
            return LocalResult<bool>.NotFound();
        }

        company.Calendar = mode;
        company.UpdatedAtUtc = UtcNow;
        await db.SaveChangesAsync(ct);
        return LocalResult<bool>.Success(true, 204);
    }

    public async Task<LocalResult<AttachmentFile>> GetLocalLogoAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var company = await db.Companies.AsNoTracking().SingleOrDefaultAsync(c => c.Id == companyId && !c.IsDeleted, ct);
        if (company?.LogoStorageKey is not { } key || company.LogoContentType is not { } contentType)
        {
            return LocalResult<AttachmentFile>.NotFound();
        }

        var path = SafeFilePath(companyId, key);
        if (path is null || !File.Exists(path))
        {
            return LocalResult<AttachmentFile>.NotFound();
        }

        return LocalResult<AttachmentFile>.Success(new AttachmentFile(key, contentType, await File.ReadAllBytesAsync(path, ct)));
    }

    public async Task<LocalResult<bool>> SaveLocalLogoAsync(Guid companyId, byte[] bytes, string contentType, CancellationToken ct = default)
    {
        if (!AllowedLogoTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
        {
            return LocalResult<bool>.BadRequest("Logo must be a JPEG, PNG or WebP image.");
        }

        if (bytes.Length == 0 || bytes.Length > MaxLogoBytes)
        {
            return LocalResult<bool>.BadRequest("Logo too large. Maximum 2 MB.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == companyId && !c.IsDeleted, ct);
        if (company is null)
        {
            return LocalResult<bool>.NotFound();
        }

        var ext = contentType.ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => ".img",
        };
        var key = $"logo_{Guid.CreateVersion7():N}{ext}";
        var folder = CompanyFilesFolder(companyId);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, key);
        await File.WriteAllBytesAsync(path, bytes, ct);

        var oldKey = company.LogoStorageKey;
        company.LogoStorageKey = key;
        company.LogoContentType = contentType.ToLowerInvariant();
        company.UpdatedAtUtc = UtcNow;
        await db.SaveChangesAsync(ct);

        DeleteQuietly(companyId, oldKey);
        return LocalResult<bool>.Success(true, 204);
    }

    public async Task<LocalResult<bool>> DeleteLocalLogoAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == companyId && !c.IsDeleted, ct);
        if (company is null)
        {
            return LocalResult<bool>.NotFound();
        }

        var oldKey = company.LogoStorageKey;
        company.LogoStorageKey = null;
        company.LogoContentType = null;
        company.UpdatedAtUtc = UtcNow;
        await db.SaveChangesAsync(ct);
        DeleteQuietly(companyId, oldKey);
        return LocalResult<bool>.Success(true, 204);
    }

    // ---------------- Parties ----------------

    public async Task<LocalResult<List<PartyDto>>> GetPartiesAsync(Guid companyId, IReadOnlySet<PartyType> visibleTypes, CancellationToken ct = default)
    {
        if (visibleTypes.Count == 0)
        {
            return LocalResult<List<PartyDto>>.Forbidden("You don't have permission to view parties.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var allowed = visibleTypes.Select(t => (PartyType?)t).ToList();
        var parties = await db.Accounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.Type == AccountType.Party && allowed.Contains(a.PartyType))
            .OrderBy(a => a.Name)
            .ToListAsync(ct);
        var balances = await db.GetBalancesAsync(companyId, ct);
        var categories = await db.PartyCategories.AsNoTracking()
            .Where(c => c.CompanyId == companyId)
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        return LocalResult<List<PartyDto>>.Success(parties.Select(p => ToPartyDto(p, balances.GetValueOrDefault(p.Id), categories)).ToList());
    }

    public async Task<LocalResult<PartyDto>> GetPartyAsync(Guid companyId, Guid partyId, IReadOnlySet<PartyType> visibleTypes, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var party = await db.Accounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == partyId && a.CompanyId == companyId && a.Type == AccountType.Party && !a.IsDeleted, ct);
        if (party is null)
        {
            return LocalResult<PartyDto>.NotFound();
        }

        if (party.PartyType is not { } pt || !visibleTypes.Contains(pt))
        {
            return LocalResult<PartyDto>.Forbidden("You don't have permission to view this party.");
        }

        var categories = party.PartyCategoryId is { } cid
            ? await db.PartyCategories.AsNoTracking().Where(c => c.Id == cid).ToDictionaryAsync(c => c.Id, c => c.Name, ct)
            : [];
        var balance = await db.GetBalanceAsync(party, ct);
        return LocalResult<PartyDto>.Success(ToPartyDto(party, balance, categories));
    }

    public async Task<LocalResult<PartyDto>> AddPartyAsync(Guid companyId, SavePartyRequest request, Guid userId, bool enqueueOutbox, CancellationToken ct = default)
    {
        if (MasterValidation.Validate(request) is { Count: > 0 } errors)
        {
            return LocalResult<PartyDto>.BadRequest(JoinErrors(errors));
        }

        var id = request.Id ?? Guid.CreateVersion7();
        var reqWithId = request with { Id = id };
        var type = MasterValidation.ParsePartyType(request.PartyType)!.Value;
        var side = MasterValidation.ParseOpeningSide(request.OpeningSide)!.Value;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct) is { } existing)
        {
            if (existing.CompanyId == companyId && existing.Type == AccountType.Party && !existing.IsDeleted)
            {
                var existingCats = existing.PartyCategoryId is { } ecid
                    ? await db.PartyCategories.AsNoTracking().Where(c => c.Id == ecid).ToDictionaryAsync(c => c.Id, c => c.Name, ct)
                    : [];
                return LocalResult<PartyDto>.Success(ToPartyDto(existing, await db.GetBalanceAsync(existing, ct), existingCats));
            }

            return LocalResult<PartyDto>.Conflict("That id is already in use.");
        }

        if (request.CategoryId is { } catId && !await db.PartyCategories.AnyAsync(c => c.Id == catId && c.CompanyId == companyId && !c.IsDeleted, ct))
        {
            return LocalResult<PartyDto>.BadRequest("That party category doesn't exist.");
        }

        var today = await CompanyTodayAsync(db, companyId, ct);
        var asOf = request.OpeningAsOf ?? today;
        var now = UtcNow;
        var party = new Account
        {
            Id = id,
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
            OpeningSide = side,
            OpeningAsOf = asOf,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedBy = userId,
            UpdatedBy = userId,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Accounts.Add(party);
        if (enqueueOutbox)
        {
            db.Outbox.Add(new OutboxItem
            {
                CompanyId = companyId,
                EntityType = "Party",
                EntityId = party.Id,
                Operation = "Create",
                PayloadJson = JsonSerializer.Serialize(reqWithId with { OpeningAsOf = asOf }, SyncJson),
                BaseVersion = 0,
                CreatedAtUtc = now,
            });
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var cats = party.PartyCategoryId is { } pcid
            ? await db.PartyCategories.AsNoTracking().Where(c => c.Id == pcid).ToDictionaryAsync(c => c.Id, c => c.Name, ct)
            : [];
        return LocalResult<PartyDto>.Success(ToPartyDto(party, Balances.SignedOpening(party), cats), 201);
    }

    public async Task<LocalResult<PartyDto>> UpdatePartyAsync(Guid companyId, Guid partyId, SavePartyRequest request, Guid userId, bool enqueueOutbox, CancellationToken ct = default)
    {
        using var held = await LockQueueAsync(ct);
        if (MasterValidation.Validate(request) is { Count: > 0 } errors)
        {
            return LocalResult<PartyDto>.BadRequest(JoinErrors(errors));
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var party = await db.Accounts.SingleOrDefaultAsync(a => a.Id == partyId && a.CompanyId == companyId && a.Type == AccountType.Party && !a.IsDeleted, ct);
        if (party is null)
        {
            return LocalResult<PartyDto>.NotFound();
        }

        if (request.CategoryId is { } catId && !await db.PartyCategories.AnyAsync(c => c.Id == catId && c.CompanyId == companyId && !c.IsDeleted, ct))
        {
            return LocalResult<PartyDto>.BadRequest("That party category doesn't exist.");
        }

        var newType = MasterValidation.ParsePartyType(request.PartyType)!.Value;
        var side = MasterValidation.ParseOpeningSide(request.OpeningSide)!.Value;
        var today = await CompanyTodayAsync(db, companyId, ct);
        var amountChanged = party.OpeningBalance != request.OpeningBalance || party.OpeningSide != side;
        var asOf = request.OpeningAsOf ?? (amountChanged ? today : party.OpeningAsOf ?? today);
        var baseVersion = party.Version;
        var now = UtcNow;

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
        party.UpdatedAtUtc = now;
        party.UpdatedBy = userId;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (enqueueOutbox)
        {
            var effectiveReq = request with { Id = partyId, OpeningAsOf = asOf };
            var pending = await db.Outbox
                .Where(o => o.CompanyId == companyId && o.EntityType == "Party" && o.EntityId == partyId)
                .OrderByDescending(o => o.Id)
                .FirstOrDefaultAsync(ct);
            pending = pending is not null && CanRewrite(pending) ? pending : null;
            if (pending is { Operation: "Create" })
            {
                pending.PayloadJson = JsonSerializer.Serialize(effectiveReq, SyncJson);
                pending.LastError = null;
            }
            else if (pending is { Operation: "Update" })
            {
                pending.PayloadJson = JsonSerializer.Serialize(new SyncPartyUpdatePayload(partyId, effectiveReq), SyncJson);
                pending.LastError = null;
            }
            else
            {
                db.Outbox.Add(new OutboxItem
                {
                    CompanyId = companyId,
                    EntityType = "Party",
                    EntityId = partyId,
                    Operation = "Update",
                    PayloadJson = JsonSerializer.Serialize(new SyncPartyUpdatePayload(partyId, effectiveReq), SyncJson),
                    BaseVersion = baseVersion,
                    CreatedAtUtc = now,
                });
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var cats = party.PartyCategoryId is { } pcid
            ? await db.PartyCategories.AsNoTracking().Where(c => c.Id == pcid).ToDictionaryAsync(c => c.Id, c => c.Name, ct)
            : [];
        return LocalResult<PartyDto>.Success(ToPartyDto(party, await db.GetBalanceAsync(party, ct), cats));
    }

    public async Task<LocalResult<bool>> DeletePartyAsync(Guid companyId, Guid partyId, Guid userId, bool enqueueOutbox, CancellationToken ct = default)
    {
        using var held = await LockQueueAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var party = await db.Accounts.SingleOrDefaultAsync(a => a.Id == partyId && a.CompanyId == companyId && a.Type == AccountType.Party && !a.IsDeleted, ct);
        if (party is null)
        {
            return LocalResult<bool>.NotFound();
        }

        if (await db.IsUsedByTransactionsAsync(companyId, partyId, ct))
        {
            return LocalResult<bool>.Conflict(MasterRules.PartyInUse);
        }

        var baseVersion = party.Version;
        var now = UtcNow;
        party.IsDeleted = true;
        party.UpdatedAtUtc = now;
        party.UpdatedBy = userId;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (enqueueOutbox)
        {
            await EnqueueDeleteOrCoalesceAsync(db, companyId, "Party", partyId, baseVersion, now, ct);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return LocalResult<bool>.Success(true, 204);
    }

    public async Task<LocalResult<LedgerDto>> GetPartyLedgerAsync(Guid companyId, Guid partyId, IReadOnlySet<PartyType> visiblePartyTypes, IReadOnlySet<TransactionType> visibleTxnTypes, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ledger = await db.GetLedgerAsync(companyId, partyId, ct);
        if (ledger is null || ledger.Account.Type != AccountType.Party)
        {
            return LocalResult<LedgerDto>.NotFound();
        }

        if (ledger.Account.PartyType is not { } pt || !visiblePartyTypes.Contains(pt))
        {
            return LocalResult<LedgerDto>.Forbidden("You don't have permission to view this party.");
        }

        return LocalResult<LedgerDto>.Success(await BuildLedgerDtoAsync(db, companyId, ledger, visibleTxnTypes, ct));
    }

    // ---------------- Party categories ----------------

    public async Task<LocalResult<List<PartyCategoryDto>>> GetPartyCategoriesAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var categories = await db.PartyCategories.AsNoTracking()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted)
            .OrderBy(c => c.Name)
            .Select(c => new PartyCategoryDto(c.Id, c.Name))
            .ToListAsync(ct);
        return LocalResult<List<PartyCategoryDto>>.Success(categories);
    }

    public async Task<LocalResult<PartyCategoryDto>> AddPartyCategoryAsync(Guid companyId, SavePartyCategoryRequest request, Guid userId, bool enqueueOutbox, CancellationToken ct = default)
    {
        if (MasterValidation.Validate(request) is { Count: > 0 } errors)
        {
            return LocalResult<PartyCategoryDto>.BadRequest(JoinErrors(errors));
        }

        var id = request.Id ?? Guid.CreateVersion7();
        var name = request.Name.Trim();
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        if (await db.PartyCategories.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) is { } existing)
        {
            return existing.CompanyId == companyId && !existing.IsDeleted
                ? LocalResult<PartyCategoryDto>.Success(new PartyCategoryDto(existing.Id, existing.Name))
                : LocalResult<PartyCategoryDto>.Conflict("That id is already in use.");
        }

        var existingNames = await db.PartyCategories.AsNoTracking()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted)
            .Select(c => c.Name)
            .ToListAsync(ct);
        if (existingNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return LocalResult<PartyCategoryDto>.Conflict($"The category \"{name}\" already exists.");
        }

        var now = UtcNow;
        var cat = new PartyCategory
        {
            Id = id,
            CompanyId = companyId,
            Name = name,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedBy = userId,
            UpdatedBy = userId,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.PartyCategories.Add(cat);
        if (enqueueOutbox)
        {
            db.Outbox.Add(new OutboxItem
            {
                CompanyId = companyId,
                EntityType = "PartyCategory",
                EntityId = cat.Id,
                Operation = "Create",
                PayloadJson = JsonSerializer.Serialize(new SavePartyCategoryRequest(name, id), SyncJson),
                BaseVersion = 0,
                CreatedAtUtc = now,
            });
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return LocalResult<PartyCategoryDto>.Success(new PartyCategoryDto(cat.Id, cat.Name), 201);
    }

    // ---------------- Money accounts ----------------

    public async Task<LocalResult<List<MoneyAccountDto>>> GetMoneyAccountsAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted
                && (a.Type == AccountType.Cash || a.Type == AccountType.Bank || a.Type == AccountType.Cheque || a.Type == AccountType.Wallet))
            .ToListAsync(ct);
        var balances = await db.GetBalancesAsync(companyId, ct);
        return LocalResult<List<MoneyAccountDto>>.Success(accounts
            .OrderBy(a => MoneyTypeOrder(a.Type)).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => ToMoneyAccountDto(a, balances.GetValueOrDefault(a.Id)))
            .ToList());
    }

    public async Task<LocalResult<MoneyAccountDto>> GetMoneyAccountAsync(Guid companyId, Guid accountId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var account = await db.Accounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == accountId && a.CompanyId == companyId && !a.IsDeleted
                && (a.Type == AccountType.Cash || a.Type == AccountType.Bank || a.Type == AccountType.Cheque || a.Type == AccountType.Wallet), ct);
        if (account is null)
        {
            return LocalResult<MoneyAccountDto>.NotFound();
        }

        return LocalResult<MoneyAccountDto>.Success(ToMoneyAccountDto(account, await db.GetBalanceAsync(account, ct)));
    }

    public async Task<LocalResult<MoneyAccountDto>> AddMoneyAccountAsync(Guid companyId, SaveMoneyAccountRequest request, Guid userId, bool enqueueOutbox, CancellationToken ct = default)
    {
        if (MasterValidation.Validate(request, isNew: true) is { Count: > 0 } errors)
        {
            return LocalResult<MoneyAccountDto>.BadRequest(JoinErrors(errors));
        }

        var id = request.Id ?? Guid.CreateVersion7();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct) is { } existing)
        {
            return existing.CompanyId == companyId && existing.IsMoneyAccount && !existing.IsDeleted
                ? LocalResult<MoneyAccountDto>.Success(ToMoneyAccountDto(existing, await db.GetBalanceAsync(existing, ct)))
                : LocalResult<MoneyAccountDto>.Conflict("That id is already in use.");
        }

        var today = await CompanyTodayAsync(db, companyId, ct);
        var asOf = request.OpeningAsOf ?? today;
        var type = MasterValidation.ParseMoneyType(request.Type)!.Value;
        var hasBank = type is AccountType.Bank or AccountType.Wallet;
        var now = UtcNow;
        var account = new Account
        {
            Id = id,
            CompanyId = companyId,
            Name = request.Name.Trim(),
            Type = type,
            AccountHolderName = hasBank ? MasterValidation.Clean(request.AccountHolderName) : null,
            AccountNumber = hasBank ? MasterValidation.Clean(request.AccountNumber) : null,
            OpeningBalance = Math.Abs(request.OpeningBalance),
            OpeningSide = request.OpeningBalance < 0 ? BalanceSide.Credit : BalanceSide.Debit,
            OpeningAsOf = asOf,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedBy = userId,
            UpdatedBy = userId,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Accounts.Add(account);
        if (enqueueOutbox)
        {
            db.Outbox.Add(new OutboxItem
            {
                CompanyId = companyId,
                EntityType = "MoneyAccount",
                EntityId = account.Id,
                Operation = "Create",
                PayloadJson = JsonSerializer.Serialize(request with { Id = id, OpeningAsOf = asOf }, SyncJson),
                BaseVersion = 0,
                CreatedAtUtc = now,
            });
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return LocalResult<MoneyAccountDto>.Success(ToMoneyAccountDto(account, Balances.SignedOpening(account)), 201);
    }

    public async Task<LocalResult<MoneyAccountDto>> UpdateMoneyAccountAsync(Guid companyId, Guid accountId, SaveMoneyAccountRequest request, Guid userId, bool enqueueOutbox, CancellationToken ct = default)
    {
        using var held = await LockQueueAsync(ct);
        if (MasterValidation.Validate(request, isNew: false) is { Count: > 0 } errors)
        {
            return LocalResult<MoneyAccountDto>.BadRequest(JoinErrors(errors));
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == accountId && a.CompanyId == companyId && !a.IsDeleted
            && (a.Type == AccountType.Cash || a.Type == AccountType.Bank || a.Type == AccountType.Cheque || a.Type == AccountType.Wallet), ct);
        if (account is null)
        {
            return LocalResult<MoneyAccountDto>.NotFound();
        }

        var type = MasterValidation.ParseMoneyType(request.Type)!.Value;
        if (type != account.Type && (account.IsSystem || !MasterValidation.AddableMoneyTypes.Contains(type)))
        {
            return LocalResult<MoneyAccountDto>.BadRequest("This account's type can't be changed.");
        }

        var today = await CompanyTodayAsync(db, companyId, ct);
        var amountChanged = Balances.SignedOpening(account) != request.OpeningBalance;
        var asOf = request.OpeningAsOf ?? (amountChanged ? today : account.OpeningAsOf ?? today);
        var baseVersion = account.Version;
        var hasBank = type is AccountType.Bank or AccountType.Wallet;
        var now = UtcNow;

        account.Name = request.Name.Trim();
        account.Type = type;
        account.AccountHolderName = hasBank ? MasterValidation.Clean(request.AccountHolderName) : null;
        account.AccountNumber = hasBank ? MasterValidation.Clean(request.AccountNumber) : null;
        account.OpeningBalance = Math.Abs(request.OpeningBalance);
        account.OpeningSide = request.OpeningBalance < 0 ? BalanceSide.Credit : BalanceSide.Debit;
        account.OpeningAsOf = asOf;
        account.UpdatedAtUtc = now;
        account.UpdatedBy = userId;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (enqueueOutbox)
        {
            var effectiveReq = request with { Id = accountId, OpeningAsOf = asOf };
            var pending = await db.Outbox
                .Where(o => o.CompanyId == companyId && o.EntityType == "MoneyAccount" && o.EntityId == accountId)
                .OrderByDescending(o => o.Id)
                .FirstOrDefaultAsync(ct);
            pending = pending is not null && CanRewrite(pending) ? pending : null;
            if (pending is { Operation: "Create" })
            {
                pending.PayloadJson = JsonSerializer.Serialize(effectiveReq, SyncJson);
                pending.LastError = null;
            }
            else if (pending is { Operation: "Update" })
            {
                pending.PayloadJson = JsonSerializer.Serialize(new SyncMoneyAccountUpdatePayload(accountId, effectiveReq), SyncJson);
                pending.LastError = null;
            }
            else
            {
                db.Outbox.Add(new OutboxItem
                {
                    CompanyId = companyId,
                    EntityType = "MoneyAccount",
                    EntityId = accountId,
                    Operation = "Update",
                    PayloadJson = JsonSerializer.Serialize(new SyncMoneyAccountUpdatePayload(accountId, effectiveReq), SyncJson),
                    BaseVersion = baseVersion,
                    CreatedAtUtc = now,
                });
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return LocalResult<MoneyAccountDto>.Success(ToMoneyAccountDto(account, await db.GetBalanceAsync(account, ct)));
    }

    public async Task<LocalResult<bool>> DeleteMoneyAccountAsync(Guid companyId, Guid accountId, Guid userId, bool enqueueOutbox, CancellationToken ct = default)
    {
        using var held = await LockQueueAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == accountId && a.CompanyId == companyId && !a.IsDeleted
            && (a.Type == AccountType.Cash || a.Type == AccountType.Bank || a.Type == AccountType.Cheque || a.Type == AccountType.Wallet), ct);
        if (account is null)
        {
            return LocalResult<bool>.NotFound();
        }

        if (MasterRules.CheckDeleteMoneyAccount(account, await db.IsUsedByTransactionsAsync(companyId, accountId, ct)) is { } blocked)
        {
            return LocalResult<bool>.Conflict(blocked);
        }

        var baseVersion = account.Version;
        var now = UtcNow;
        account.IsDeleted = true;
        account.UpdatedAtUtc = now;
        account.UpdatedBy = userId;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (enqueueOutbox)
        {
            await EnqueueDeleteOrCoalesceAsync(db, companyId, "MoneyAccount", accountId, baseVersion, now, ct);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return LocalResult<bool>.Success(true, 204);
    }

    public async Task<LocalResult<LedgerDto>> GetAccountLedgerAsync(Guid companyId, Guid accountId, IReadOnlySet<TransactionType> visibleTxnTypes, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ledger = await db.GetLedgerAsync(companyId, accountId, ct);
        if (ledger is null || !ledger.Account.IsMoneyAccount)
        {
            return LocalResult<LedgerDto>.NotFound();
        }

        return LocalResult<LedgerDto>.Success(await BuildLedgerDtoAsync(db, companyId, ledger, visibleTxnTypes, ct));
    }

    // ---------------- Income / Expense heads ----------------

    public async Task<LocalResult<List<CategoryDto>>> GetCategoriesAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var heads = await db.Accounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.IsActive && (a.Type == AccountType.Income || a.Type == AccountType.Expense))
            .OrderBy(a => a.Name)
            .ToListAsync(ct);
        return LocalResult<List<CategoryDto>>.Success(heads.Select(a => new CategoryDto(a.Id, a.Name, a.Type.ToString())).ToList());
    }

    public async Task<LocalResult<CategoryDto>> AddCategoryAsync(Guid companyId, SaveCategoryRequest request, Guid userId, bool enqueueOutbox, CancellationToken ct = default)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 100)
        {
            return LocalResult<CategoryDto>.BadRequest("Enter a name of up to 100 characters.");
        }

        AccountType? type = request.Type switch
        {
            nameof(AccountType.Expense) => AccountType.Expense,
            nameof(AccountType.Income) => AccountType.Income,
            _ => null,
        };
        if (type is not { } headType)
        {
            return LocalResult<CategoryDto>.BadRequest("Choose Expense or Income.");
        }

        var id = request.Id == Guid.Empty ? Guid.CreateVersion7() : request.Id;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct) is { } existing)
        {
            return existing.CompanyId == companyId && existing.Type == headType && !existing.IsDeleted
                ? LocalResult<CategoryDto>.Success(new CategoryDto(existing.Id, existing.Name, existing.Type.ToString()))
                : LocalResult<CategoryDto>.Conflict("That id is already in use.");
        }

        var names = await db.Accounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.Type == headType)
            .Select(a => a.Name)
            .ToListAsync(ct);
        if (names.Contains(name, StringComparer.CurrentCultureIgnoreCase))
        {
            return LocalResult<CategoryDto>.Conflict("There is already a category with that name.");
        }

        var now = UtcNow;
        var account = new Account
        {
            Id = id,
            CompanyId = companyId,
            Name = name,
            Type = headType,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedBy = userId,
            UpdatedBy = userId,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Accounts.Add(account);
        if (enqueueOutbox)
        {
            db.Outbox.Add(new OutboxItem
            {
                CompanyId = companyId,
                EntityType = "Category",
                EntityId = account.Id,
                Operation = "Create",
                PayloadJson = JsonSerializer.Serialize(new SaveCategoryRequest(name, headType.ToString(), id), SyncJson),
                BaseVersion = 0,
                CreatedAtUtc = now,
            });
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return LocalResult<CategoryDto>.Success(new CategoryDto(account.Id, account.Name, headType.ToString()), 201);
    }

    // ---------------- Transactions & Adjustments ----------------

    public async Task<LocalResult<List<TransactionDto>>> GetTransactionsAsync(Guid companyId, IReadOnlySet<TransactionType> visibleTypes, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var allowed = visibleTypes.ToList();
        var txns = await db.Transactions.AsNoTracking()
            .Where(t => t.CompanyId == companyId && !t.IsDeleted && t.Status == TransactionStatus.Posted && allowed.Contains(t.Type))
            .OrderByDescending(t => t.Date).ThenByDescending(t => t.CreatedAtUtc)
            .ToListAsync(ct);
        return LocalResult<List<TransactionDto>>.Success(txns.Select(ToTransactionDto).ToList());
    }

    public async Task<LocalResult<TransactionDto>> GetTransactionAsync(Guid companyId, Guid transactionId, IReadOnlySet<TransactionType> visibleTypes, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var txn = await db.Transactions.AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == transactionId && t.CompanyId == companyId && !t.IsDeleted, ct);
        if (txn is null)
        {
            return LocalResult<TransactionDto>.NotFound();
        }

        return visibleTypes.Contains(txn.Type)
            ? LocalResult<TransactionDto>.Success(ToTransactionDto(txn))
            : LocalResult<TransactionDto>.Forbidden("You don't have permission to view this entry.");
    }

    public async Task<LocalResult<TransactionDto>> AddTransactionAsync(
        Guid companyId, CreateTransactionRequest request, Guid userId, bool enqueueOutbox, IReadOnlySet<TransactionType>? visibleTypes = null, CancellationToken ct = default)
    {
        if (MasterValidation.Validate(request) is { Count: > 0 } errors)
        {
            return LocalResult<TransactionDto>.BadRequest(JoinErrors(errors));
        }

        var type = MasterValidation.ParseTransactionType(request.Type)!.Value;
        var id = request.Id ?? Guid.CreateVersion7();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Transactions.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct) is { } existing)
        {
            return existing.CompanyId == companyId && !existing.IsDeleted
                ? LocalResult<TransactionDto>.Success(ToTransactionDto(existing))
                : LocalResult<TransactionDto>.Conflict("That id is already in use.");
        }

        var today = await CompanyTodayAsync(db, companyId, ct);
        var date = request.Date ?? today;
        if (date > today)
        {
            return LocalResult<TransactionDto>.BadRequest("The date can't be in the future.");
        }

        var now = UtcNow;
        var txn = new Transaction
        {
            Id = id,
            CompanyId = companyId,
            Type = type,
            VoucherNo = id.ToString(),
            IsProvisionalNo = true,
            Date = date,
            Amount = request.Amount,
            FromAccountId = request.FromAccountId,
            ToAccountId = request.ToAccountId,
            Remarks = MasterValidation.Clean(request.Remarks),
            ChequeNo = MasterValidation.Clean(request.ChequeNo),
            ChequeDate = request.ChequeDate,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedBy = userId,
            UpdatedBy = userId,
        };

        if (await ValidatePostingAsync(db, companyId, txn, ct) is { } postingError)
        {
            return LocalResult<TransactionDto>.BadRequest(postingError);
        }

        if (request.CheckForDuplicates && await FindDuplicatesAsync(db, companyId, txn, visibleTypes, ct) is { Count: > 0 } dups)
        {
            return LocalResult<TransactionDto>.Duplicate(new DuplicateTransactionWarning(dups.Select(ToTransactionDto).ToList()));
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Transactions.Add(txn);
        if (enqueueOutbox)
        {
            var payloadReq = request with { Id = id, Date = date, CheckForDuplicates = false };
            db.Outbox.Add(new OutboxItem
            {
                CompanyId = companyId,
                EntityType = "Transaction",
                EntityId = txn.Id,
                Operation = "Create",
                PayloadJson = JsonSerializer.Serialize(payloadReq, SyncJson),
                BaseVersion = 0,
                CreatedAtUtc = now,
            });
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return LocalResult<TransactionDto>.Success(ToTransactionDto(txn), 201);
    }

    public async Task<LocalResult<TransactionDto>> UpdateTransactionAsync(
        Guid companyId, Guid transactionId, UpdateTransactionRequest request, Guid userId, bool enqueueOutbox, IReadOnlySet<TransactionType>? visibleTypes = null, CancellationToken ct = default)
    {
        using var held = await LockQueueAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var txn = await db.Transactions.SingleOrDefaultAsync(t => t.Id == transactionId && t.CompanyId == companyId && !t.IsDeleted, ct);
        if (txn is null)
        {
            return LocalResult<TransactionDto>.NotFound("That entry doesn't exist.");
        }

        if (txn.Status != TransactionStatus.Posted)
        {
            return LocalResult<TransactionDto>.NotFound("That entry was voided.");
        }

        if (MasterValidation.Validate(request) is { Count: > 0 } errors)
        {
            return LocalResult<TransactionDto>.BadRequest(JoinErrors(errors));
        }

        if (request.Type is not null && MasterValidation.ParseTransactionType(request.Type) != txn.Type)
        {
            return LocalResult<TransactionDto>.BadRequest("An entry's type can't be changed. Void it and add a new one.");
        }

        var today = await CompanyTodayAsync(db, companyId, ct);
        if (request.Date == default || request.Date > today)
        {
            return LocalResult<TransactionDto>.BadRequest("The date can't be in the future.");
        }

        var checkDuplicates = request.CheckForDuplicates
            && (txn.Date != request.Date || txn.Amount != request.Amount
                || PartyIdFor(txn.Type, txn.FromAccountId, txn.ToAccountId) != PartyIdFor(txn.Type, request.FromAccountId, request.ToAccountId));

        var baseVersion = txn.Version;
        var now = UtcNow;
        txn.Date = request.Date;
        txn.Amount = request.Amount;
        txn.FromAccountId = request.FromAccountId;
        txn.ToAccountId = request.ToAccountId;
        txn.Remarks = MasterValidation.Clean(request.Remarks);
        txn.ChequeNo = MasterValidation.Clean(request.ChequeNo);
        txn.ChequeDate = request.ChequeDate;
        txn.UpdatedAtUtc = now;
        txn.UpdatedBy = userId;

        if (await ValidatePostingAsync(db, companyId, txn, ct) is { } postingError)
        {
            return LocalResult<TransactionDto>.BadRequest(postingError);
        }

        if (checkDuplicates && await FindDuplicatesAsync(db, companyId, txn, visibleTypes, ct) is { Count: > 0 } dups)
        {
            return LocalResult<TransactionDto>.Duplicate(new DuplicateTransactionWarning(dups.Select(ToTransactionDto).ToList()));
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (enqueueOutbox)
        {
            var effectiveUpdate = request with { CheckForDuplicates = false };
            var pending = await db.Outbox
                .Where(o => o.CompanyId == companyId && o.EntityType == "Transaction" && o.EntityId == transactionId)
                .OrderByDescending(o => o.Id)
                .FirstOrDefaultAsync(ct);
            pending = pending is not null && CanRewrite(pending) ? pending : null;
            if (pending is { Operation: "Create" })
            {
                var mergedCreate = new CreateTransactionRequest(
                    txn.Type.ToString(), txn.Date, txn.Amount, txn.FromAccountId, txn.ToAccountId,
                    txn.Remarks, txn.ChequeNo, txn.ChequeDate, txn.Id, CheckForDuplicates: false);
                pending.PayloadJson = JsonSerializer.Serialize(mergedCreate, SyncJson);
                pending.LastError = null;
            }
            else if (pending is { Operation: "Update" })
            {
                pending.PayloadJson = JsonSerializer.Serialize(new SyncUpdatePayload(transactionId, effectiveUpdate), SyncJson);
                pending.LastError = null;
            }
            else
            {
                db.Outbox.Add(new OutboxItem
                {
                    CompanyId = companyId,
                    EntityType = "Transaction",
                    EntityId = transactionId,
                    Operation = "Update",
                    PayloadJson = JsonSerializer.Serialize(new SyncUpdatePayload(transactionId, effectiveUpdate), SyncJson),
                    BaseVersion = baseVersion,
                    CreatedAtUtc = now,
                });
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return LocalResult<TransactionDto>.Success(ToTransactionDto(txn));
    }

    public async Task<LocalResult<bool>> VoidTransactionAsync(Guid companyId, Guid transactionId, Guid userId, bool enqueueOutbox, CancellationToken ct = default)
    {
        using var held = await LockQueueAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var txn = await db.Transactions.SingleOrDefaultAsync(t => t.Id == transactionId && t.CompanyId == companyId && !t.IsDeleted, ct);
        if (txn is null)
        {
            return LocalResult<bool>.NotFound("That entry doesn't exist.");
        }

        if (txn.Status == TransactionStatus.Void)
        {
            return LocalResult<bool>.Success(true, 204);
        }

        var baseVersion = txn.Version;
        var now = UtcNow;
        txn.Status = TransactionStatus.Void;
        txn.UpdatedAtUtc = now;
        txn.UpdatedBy = userId;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (enqueueOutbox)
        {
            var pending = await db.Outbox
                .Where(o => o.CompanyId == companyId && (o.EntityType == "Transaction" || o.EntityType == "Adjustment") && o.EntityId == transactionId)
                .ToListAsync(ct);
            var sent = pending.Any(o => !CanRewrite(o));
            var neverReached = !sent && pending.Any(o => o.Operation == "Create");
            if (!sent)
            {
                db.Outbox.RemoveRange(pending);
            }

            if (!neverReached)
            {
                db.Outbox.Add(new OutboxItem
                {
                    CompanyId = companyId,
                    EntityType = "Transaction",
                    EntityId = transactionId,
                    Operation = "Void",
                    PayloadJson = JsonSerializer.Serialize(new SyncVoidPayload(transactionId), SyncJson),
                    BaseVersion = baseVersion,
                    CreatedAtUtc = now,
                });
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return LocalResult<bool>.Success(true, 204);
    }

    public async Task<LocalResult<bool>> AdjustBalanceAsync(Guid companyId, Guid accountId, AdjustBalanceRequest request, Guid userId, bool enqueueOutbox, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var account = await db.Accounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == accountId && a.CompanyId == companyId && !a.IsDeleted, ct);
        if (account is null || !(account.Type == AccountType.Party || account.IsMoneyAccount))
        {
            return LocalResult<bool>.NotFound("That account doesn't exist.");
        }

        var system = await db.Accounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.Type == AccountType.System)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(ct);
        if (system is not { } adjustmentId)
        {
            return LocalResult<bool>.Conflict("This company has no adjustment account.");
        }

        var type = account.Type == AccountType.Party
            ? request.Increase ? TransactionType.PartyAdjustAdd : TransactionType.PartyAdjustReduce
            : request.Increase ? TransactionType.AddMoney : TransactionType.ReduceMoney;
        var (from, to) = request.Increase ? (adjustmentId, account.Id) : (account.Id, adjustmentId);

        var id = request.Id == Guid.Empty ? Guid.CreateVersion7() : request.Id;
        var today = await CompanyTodayAsync(db, companyId, ct);
        var date = request.Date ?? today;
        var createReq = new CreateTransactionRequest(type.ToString(), date, request.Amount, from, to, request.Remarks, null, null, id);
        if (MasterValidation.Validate(createReq) is { Count: > 0 } errors)
        {
            return LocalResult<bool>.BadRequest(JoinErrors(errors));
        }

        if (date > today)
        {
            return LocalResult<bool>.BadRequest("The date can't be in the future.");
        }

        var now = UtcNow;
        var txn = new Transaction
        {
            Id = id,
            CompanyId = companyId,
            Type = type,
            VoucherNo = id.ToString(),
            IsProvisionalNo = true,
            Date = date,
            Amount = request.Amount,
            FromAccountId = from,
            ToAccountId = to,
            Remarks = MasterValidation.Clean(request.Remarks),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedBy = userId,
            UpdatedBy = userId,
        };

        if (await ValidatePostingAsync(db, companyId, txn, ct) is { } postingError)
        {
            return LocalResult<bool>.BadRequest(postingError);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Transactions.Add(txn);
        if (enqueueOutbox)
        {
            db.Outbox.Add(new OutboxItem
            {
                CompanyId = companyId,
                EntityType = "Adjustment",
                EntityId = txn.Id,
                Operation = "Create",
                PayloadJson = JsonSerializer.Serialize(new SyncAdjustmentPayload(accountId, request with { Id = id, Date = date }), SyncJson),
                BaseVersion = 0,
                CreatedAtUtc = now,
            });
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return LocalResult<bool>.Success(true, 204);
    }

    // ---------------- Attachments (Offline-first file storage + upload queue) ----------------

    public async Task<LocalResult<AttachmentDto>> SaveLocalAttachmentAsync(
        Guid companyId, Guid transactionId, string fileName, string contentType, byte[] bytes, Guid userId, bool pendingUpload, CancellationToken ct = default)
    {
        if (!AllowedAttachmentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
        {
            return LocalResult<AttachmentDto>.BadRequest("File type not allowed. Use JPEG, PNG, WebP or PDF.");
        }

        if (bytes.Length == 0 || bytes.Length > MaxAttachmentBytes)
        {
            return LocalResult<AttachmentDto>.BadRequest("File too large. Maximum 5 MB.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var txn = await db.Transactions.AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == transactionId && t.CompanyId == companyId && !t.IsDeleted && t.Status == TransactionStatus.Posted, ct);
        if (txn is null)
        {
            return LocalResult<AttachmentDto>.BadRequest("Transaction not found.");
        }

        var count = await db.Attachments.CountAsync(a => a.CompanyId == companyId && a.TransactionId == transactionId && !a.IsDeleted, ct);
        if (count >= MaxAttachmentsPerEntry)
        {
            return LocalResult<AttachmentDto>.BadRequest($"An entry can have at most {MaxAttachmentsPerEntry} attachments.");
        }

        var id = Guid.CreateVersion7();
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(ext) || ext.Length > 10)
        {
            ext = contentType.ToLowerInvariant() switch
            {
                "image/jpeg" => ".jpg",
                "image/png" => ".png",
                "image/webp" => ".webp",
                "application/pdf" => ".pdf",
                _ => ".bin",
            };
        }

        var localFileName = $"{id:N}{ext}";
        var folder = CompanyFilesFolder(companyId);
        Directory.CreateDirectory(folder);
        var localPath = Path.Combine(folder, localFileName);
        await File.WriteAllBytesAsync(localPath, bytes, ct);

        var now = UtcNow;
        var attachment = new Attachment
        {
            Id = id,
            CompanyId = companyId,
            TransactionId = transactionId,
            FileName = fileName,
            ContentType = contentType.ToLowerInvariant(),
            SizeBytes = bytes.Length,
            StorageKey = null,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedBy = userId,
            UpdatedBy = userId,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Attachments.Add(attachment);
        db.LocalAttachmentFiles.Add(new LocalAttachmentFile
        {
            AttachmentId = id,
            LocalPath = localPath,
            UploadStatus = pendingUpload ? UploadStatus.Pending : UploadStatus.Uploaded,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return LocalResult<AttachmentDto>.Success(ToAttachmentDto(attachment), 201);
    }

    public async Task<LocalResult<List<AttachmentDto>>> GetAttachmentsAsync(Guid companyId, Guid? transactionId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.Attachments.AsNoTracking().Where(a => a.CompanyId == companyId && !a.IsDeleted);
        if (transactionId is { } tid)
        {
            query = query.Where(a => a.TransactionId == tid);
        }

        var list = await query.OrderByDescending(a => a.CreatedAtUtc).ToListAsync(ct);
        return LocalResult<List<AttachmentDto>>.Success(list.Select(ToAttachmentDto).ToList());
    }

    public async Task<AttachmentFile?> TryReadLocalAttachmentFileAsync(Guid companyId, Guid attachmentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var att = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == attachmentId && a.CompanyId == companyId && !a.IsDeleted, ct);
        if (att is null)
        {
            return null;
        }

        var local = await db.LocalAttachmentFiles.AsNoTracking().SingleOrDefaultAsync(f => f.AttachmentId == attachmentId, ct);
        if (local is null || !File.Exists(local.LocalPath))
        {
            return null;
        }

        return new AttachmentFile(att.FileName, att.ContentType, await File.ReadAllBytesAsync(local.LocalPath, ct));
    }

    public async Task CacheDownloadedAttachmentAsync(Guid companyId, Guid attachmentId, string fileName, byte[] bytes, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(ext) || ext.Length > 10) ext = ".bin";
        var folder = CompanyFilesFolder(companyId);
        Directory.CreateDirectory(folder);
        var localPath = Path.Combine(folder, $"{attachmentId:N}{ext}");
        await File.WriteAllBytesAsync(localPath, bytes, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var existing = await db.LocalAttachmentFiles.FindAsync([attachmentId], ct);
        if (existing is null)
        {
            db.LocalAttachmentFiles.Add(new LocalAttachmentFile
            {
                AttachmentId = attachmentId,
                LocalPath = localPath,
                UploadStatus = UploadStatus.Uploaded,
            });
        }
        else
        {
            existing.LocalPath = localPath;
            existing.UploadStatus = UploadStatus.Uploaded;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<LocalResult<bool>> DeleteAttachmentAsync(Guid companyId, Guid attachmentId, Guid userId, bool enqueueOutbox, CancellationToken ct = default)
    {
        using var held = await LockQueueAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var att = await db.Attachments.SingleOrDefaultAsync(a => a.Id == attachmentId && a.CompanyId == companyId && !a.IsDeleted, ct);
        if (att is null)
        {
            return LocalResult<bool>.NotFound();
        }

        var wasSynced = att.Version > 0 || att.StorageKey is not null;
        var now = UtcNow;
        att.IsDeleted = true;
        att.UpdatedAtUtc = now;
        att.UpdatedBy = userId;

        var local = await db.LocalAttachmentFiles.FindAsync([attachmentId], ct);
        if (local is not null && !wasSynced)
        {
            // Never uploaded yet: drop upload queue entry and delete local file.
            db.LocalAttachmentFiles.Remove(local);
            TryDeleteFile(local.LocalPath);
        }

        if (enqueueOutbox && wasSynced)
        {
            db.Outbox.Add(new OutboxItem
            {
                CompanyId = companyId,
                EntityType = "Attachment",
                EntityId = attachmentId,
                Operation = "Delete",
                PayloadJson = JsonSerializer.Serialize(new SyncDeletePayload(attachmentId), SyncJson),
                BaseVersion = att.Version,
                CreatedAtUtc = now,
            });
        }

        await db.SaveChangesAsync(ct);
        return LocalResult<bool>.Success(true, 204);
    }

    // ---------------- Sync issues & conflict resolution ----------------

    public async Task<List<LocalSyncIssue>> GetSyncIssuesAsync(Guid companyId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Outbox.AsNoTracking()
            .Where(o => o.CompanyId == companyId && o.LastError != null)
            .OrderBy(o => o.Id)
            .Select(o => new LocalSyncIssue(o.Id, o.CompanyId, o.EntityType, o.EntityId, o.Operation, o.Attempts, o.LastError!, o.CreatedAtUtc))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Drops a refused change and goes back to what the server has. A record the server never accepted is removed
    /// from this device too, with anything queued that depends on it.
    /// </summary>
    public async Task DiscardOutboxItemAsync(long outboxId, CancellationToken ct = default)
    {
        using var held = await LockQueueAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.Outbox.FindAsync([outboxId], ct);
        if (item is null) return;

        var queued = await db.Outbox.Where(o => o.CompanyId == item.CompanyId).OrderBy(o => o.Id).ToListAsync(ct);
        var dropped = queued.Where(o => o.EntityId == item.EntityId && o.Id >= item.Id).ToList();

        if (item.Operation == "Create")
        {
            var gone = new HashSet<Guid> { item.EntityId };
            switch (item.EntityType)
            {
                case "Transaction" or "Adjustment":
                    break;

                case "Party" or "MoneyAccount" or "Category":
                    // Entries for an account the server never had cannot be uploaded either.
                    var entries = await db.Transactions
                        .Where(t => t.CompanyId == item.CompanyId && (t.FromAccountId == item.EntityId || t.ToAccountId == item.EntityId))
                        .ToListAsync(ct);
                    gone.UnionWith(entries.Select(t => t.Id));
                    db.Transactions.RemoveRange(entries);
                    break;
            }

            dropped = queued.Where(o => gone.Contains(o.EntityId)).ToList();
            var files = await db.Attachments
                .Where(a => a.CompanyId == item.CompanyId && a.TransactionId != null && gone.Contains(a.TransactionId.Value))
                .ToListAsync(ct);
            var fileIds = files.Select(a => a.Id).ToList();
            foreach (var local in await db.LocalAttachmentFiles.Where(f => fileIds.Contains(f.AttachmentId)).ToListAsync(ct))
            {
                TryDeleteFile(local.LocalPath);
                db.LocalAttachmentFiles.Remove(local);
            }

            db.Attachments.RemoveRange(files);
            db.Transactions.RemoveRange(await db.Transactions.Where(t => gone.Contains(t.Id)).ToListAsync(ct));
            db.Accounts.RemoveRange(await db.Accounts.Where(a => a.Id == item.EntityId).ToListAsync(ct));
            if (item.EntityType == "PartyCategory")
            {
                foreach (var party in await db.Accounts.Where(a => a.PartyCategoryId == item.EntityId).ToListAsync(ct))
                {
                    party.PartyCategoryId = null;
                }

                db.PartyCategories.RemoveRange(await db.PartyCategories.Where(c => c.Id == item.EntityId).ToListAsync(ct));
            }
        }

        db.Outbox.RemoveRange(dropped);

        // Download everything again, so the server copy replaces what this device showed.
        var checkpoint = await db.SyncCheckpoints.SingleOrDefaultAsync(s => s.CompanyId == item.CompanyId, ct);
        if (checkpoint is not null)
        {
            checkpoint.LastPulledVersion = 0;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task RetryOutboxItemAsync(long outboxId, CancellationToken ct = default)
    {
        using var held = await LockQueueAsync(ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var item = await db.Outbox.FindAsync([outboxId], ct);
        if (item is null) return;
        item.LastError = null;
        item.Attempts = 0;
        await db.SaveChangesAsync(ct);
    }

    // ---------------- Offline -> Online Upgrade Snapshot & Portable JSON Backup ----------------

    public async Task<LocalDataSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return new LocalDataSummary(
            Companies: await db.Companies.CountAsync(c => !c.IsDeleted, ct),
            Parties: await db.Accounts.CountAsync(a => !a.IsDeleted && a.Type == AccountType.Party, ct),
            MoneyAccounts: await db.Accounts.CountAsync(a => !a.IsDeleted && (a.Type == AccountType.Cash || a.Type == AccountType.Bank || a.Type == AccountType.Cheque || a.Type == AccountType.Wallet), ct),
            Transactions: await db.Transactions.CountAsync(t => !t.IsDeleted && t.Status == TransactionStatus.Posted, ct),
            Attachments: await db.Attachments.CountAsync(a => !a.IsDeleted, ct));
    }

    public async Task<List<LocalCompanyMigrationBundle>> BuildMigrationBundlesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var companies = await db.Companies.AsNoTracking().Where(c => !c.IsDeleted).OrderBy(c => c.CreatedAtUtc).ToListAsync(ct);
        var bundles = new List<LocalCompanyMigrationBundle>(companies.Count);

        foreach (var c in companies)
        {
            var cats = await db.PartyCategories.AsNoTracking().Where(x => x.CompanyId == c.Id).ToListAsync(ct);
            var accounts = await db.Accounts.AsNoTracking().Where(x => x.CompanyId == c.Id).ToListAsync(ct);
            var txns = await db.Transactions.AsNoTracking().Where(x => x.CompanyId == c.Id).ToListAsync(ct);
            var atts = await db.Attachments.AsNoTracking().Where(x => x.CompanyId == c.Id && !x.IsDeleted && x.TransactionId != null).ToListAsync(ct);
            var attIds = atts.Select(a => a.Id).ToList();
            var localFiles = await db.LocalAttachmentFiles.AsNoTracking()
                .Where(f => attIds.Contains(f.AttachmentId))
                .ToDictionaryAsync(f => f.AttachmentId, ct);

            var snapshot = new MigrateOfflineCompanyRequest(
                CompanyId: c.Id,
                Name: c.Name,
                Phone: c.Phone,
                Address: c.Address,
                Pan: c.Pan,
                Calendar: c.Calendar.ToString(),
                FiscalYearStartMonth: c.FiscalYearStartMonth,
                Currency: c.Currency,
                TimeZoneId: c.TimeZoneId,
                PartyCategories: cats.Select(x => new MigratePartyCategoryDto(x.Id, x.Name, x.IsDeleted)).ToList(),
                Accounts: accounts.Select(a => new MigrateAccountDto(
                    a.Id, a.Name, a.Type.ToString(), a.IsSystem, a.IsActive,
                    a.OpeningBalance, a.OpeningSide.ToString(), a.OpeningAsOf,
                    a.PartyType?.ToString(), a.Phone, a.Email, a.Address, a.Pan,
                    a.PartyCategoryId, a.AccountHolderName, a.AccountNumber, a.IsDeleted)).ToList(),
                Transactions: txns.Select(t => new MigrateTransactionDto(
                    t.Id, t.Type.ToString(), t.VoucherNo, t.IsManualNo, t.IsProvisionalNo,
                    t.Date, t.Amount, t.FromAccountId, t.ToAccountId,
                    t.Remarks, t.ChequeNo, t.ChequeDate, t.Status.ToString(), t.IsDeleted)).ToList());

            var logoPath = c.LogoStorageKey is { } key ? SafeFilePath(c.Id, key) : null;
            if (logoPath is not null && !File.Exists(logoPath)) logoPath = null;

            var uploads = new List<LocalAttachmentToUpload>();
            foreach (var a in atts)
            {
                if (localFiles.TryGetValue(a.Id, out var lf) && File.Exists(lf.LocalPath))
                {
                    uploads.Add(new LocalAttachmentToUpload(a.Id, a.TransactionId!.Value, a.FileName, a.ContentType, lf.LocalPath));
                }
            }

            bundles.Add(new LocalCompanyMigrationBundle(snapshot, logoPath, c.LogoContentType, uploads));
        }

        return bundles;
    }

    public async Task<LocalResult<string>> ExportCompanyBackupJsonAsync(Guid companyId, CancellationToken ct = default)
    {
        var bundles = await BuildMigrationBundlesAsync(ct);
        var bundle = bundles.FirstOrDefault(b => b.Snapshot.CompanyId == companyId);
        if (bundle is null)
        {
            return LocalResult<string>.NotFound("Company not found on this device.");
        }

        var json = JsonSerializer.Serialize(bundle.Snapshot, new JsonSerializerOptions(SyncJson) { WriteIndented = true });
        return LocalResult<string>.Success(json);
    }

    public async Task<LocalResult<CompanySummary>> ImportCompanyBackupJsonAsync(string json, Guid userId, CancellationToken ct = default)
    {
        MigrateOfflineCompanyRequest? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<MigrateOfflineCompanyRequest>(json, SyncJson);
        }
        catch (JsonException)
        {
            return LocalResult<CompanySummary>.BadRequest("That file is not a valid Lekha Books backup.");
        }

        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.Name))
        {
            return LocalResult<CompanySummary>.BadRequest("Backup file is missing company details.");
        }

        // Remap all IDs on restore so importing a backup into an existing device never clashes with existing rows.
        var now = UtcNow;
        var newCompanyId = Guid.CreateVersion7();
        var cal = Enum.TryParse<CalendarMode>(snapshot.Calendar, true, out var cMode) ? cMode : CalendarMode.BS;
        var catMap = snapshot.PartyCategories.ToDictionary(c => c.Id, _ => Guid.CreateVersion7());
        var accMap = snapshot.Accounts.ToDictionary(a => a.Id, _ => Guid.CreateVersion7());

        var company = new Company
        {
            Id = newCompanyId,
            Name = snapshot.Name.Trim(),
            Phone = snapshot.Phone,
            Address = snapshot.Address,
            Pan = snapshot.Pan,
            Calendar = cal,
            FiscalYearStartMonth = snapshot.FiscalYearStartMonth is >= 1 and <= 12 ? snapshot.FiscalYearStartMonth : 4,
            Currency = string.IsNullOrWhiteSpace(snapshot.Currency) ? "NPR" : snapshot.Currency,
            TimeZoneId = string.IsNullOrWhiteSpace(snapshot.TimeZoneId) ? CompanyClock.DefaultTimeZoneId : snapshot.TimeZoneId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        var categories = snapshot.PartyCategories.Select(c => new PartyCategory
        {
            Id = catMap[c.Id],
            CompanyId = newCompanyId,
            Name = c.Name,
            IsDeleted = c.IsDeleted,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedBy = userId,
            UpdatedBy = userId,
        }).ToList();

        var accounts = new List<Account>(snapshot.Accounts.Count);
        foreach (var a in snapshot.Accounts)
        {
            if (!Enum.TryParse<AccountType>(a.Type, true, out var accType)) continue;
            var openSide = Enum.TryParse<BalanceSide>(a.OpeningSide, true, out var os) ? os : BalanceSide.Debit;
            PartyType? pt = Enum.TryParse<PartyType>(a.PartyType, true, out var parsedPt) ? parsedPt : null;
            accounts.Add(new Account
            {
                Id = accMap[a.Id],
                CompanyId = newCompanyId,
                Name = a.Name,
                Type = accType,
                IsSystem = a.IsSystem,
                IsActive = a.IsActive,
                OpeningBalance = a.OpeningBalance,
                OpeningSide = openSide,
                OpeningAsOf = a.OpeningAsOf,
                PartyType = pt,
                Phone = a.Phone,
                Email = a.Email,
                Address = a.Address,
                Pan = a.Pan,
                PartyCategoryId = a.PartyCategoryId is { } cid && catMap.TryGetValue(cid, out var newCid) ? newCid : null,
                AccountHolderName = a.AccountHolderName,
                AccountNumber = a.AccountNumber,
                IsDeleted = a.IsDeleted,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedBy = userId,
                UpdatedBy = userId,
            });
        }

        var txns = new List<Transaction>(snapshot.Transactions.Count);
        foreach (var t in snapshot.Transactions)
        {
            if (!Enum.TryParse<TransactionType>(t.Type, true, out var txnType)) continue;
            if (!accMap.TryGetValue(t.FromAccountId, out var newFrom) || !accMap.TryGetValue(t.ToAccountId, out var newTo)) continue;
            var status = Enum.TryParse<TransactionStatus>(t.Status, true, out var st) ? st : TransactionStatus.Posted;
            txns.Add(new Transaction
            {
                Id = Guid.CreateVersion7(),
                CompanyId = newCompanyId,
                Type = txnType,
                VoucherNo = t.VoucherNo,
                IsManualNo = t.IsManualNo,
                IsProvisionalNo = t.IsProvisionalNo,
                Date = t.Date,
                Amount = t.Amount,
                FromAccountId = newFrom,
                ToAccountId = newTo,
                Remarks = t.Remarks,
                ChequeNo = t.ChequeNo,
                ChequeDate = t.ChequeDate,
                Status = status,
                IsDeleted = t.IsDeleted,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedBy = userId,
                UpdatedBy = userId,
            });
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Companies.Add(company);
        db.PartyCategories.AddRange(categories);
        db.Accounts.AddRange(accounts);
        db.Transactions.AddRange(txns);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var perms = RoleTemplates.For(CompanyRole.Owner).Order(StringComparer.Ordinal).ToList();
        return LocalResult<CompanySummary>.Success(new CompanySummary(company.Id, company.Name, CompanyRole.Owner.ToString(), perms, company.Calendar.ToString()), 201);
    }

    // ---------------- Private Helpers ----------------

    private static async Task EnqueueDeleteOrCoalesceAsync(
        SqliteDbContext db, Guid companyId, string entityType, Guid entityId, long baseVersion, DateTime now, CancellationToken ct)
    {
        var pending = await db.Outbox
            .Where(o => o.CompanyId == companyId && o.EntityType == entityType && o.EntityId == entityId)
            .ToListAsync(ct);
        var sent = pending.Any(o => !CanRewrite(o));
        var neverReached = !sent && pending.Any(o => o.Operation == "Create");
        if (!sent)
        {
            db.Outbox.RemoveRange(pending);
        }

        if (!neverReached)
        {
            db.Outbox.Add(new OutboxItem
            {
                CompanyId = companyId,
                EntityType = entityType,
                EntityId = entityId,
                Operation = "Delete",
                PayloadJson = JsonSerializer.Serialize(new SyncDeletePayload(entityId), SyncJson),
                BaseVersion = baseVersion,
                CreatedAtUtc = now,
            });
        }
    }

    private async Task<DateOnly> CompanyTodayAsync(SqliteDbContext db, Guid companyId, CancellationToken ct)
    {
        var zone = await db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => c.TimeZoneId)
            .FirstOrDefaultAsync(ct) ?? CompanyClock.DefaultTimeZoneId;
        return CompanyClock.Today(zone, _clock);
    }

    private static async Task<string?> ValidatePostingAsync(SqliteDbContext db, Guid companyId, Transaction txn, CancellationToken ct)
    {
        var ids = new[] { txn.FromAccountId, txn.ToAccountId };
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && ids.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);
        if (!accounts.TryGetValue(txn.FromAccountId, out var from))
        {
            return "That account doesn't exist.";
        }

        if (!accounts.TryGetValue(txn.ToAccountId, out var to))
        {
            return "That account doesn't exist.";
        }

        var errors = PostingRules.Validate(txn, from, to);
        return errors.Count == 0 ? null : string.Join(" ", errors);
    }

    private static async Task<List<Transaction>> FindDuplicatesAsync(
        SqliteDbContext db, Guid companyId, Transaction txn, IReadOnlySet<TransactionType>? visibleTypes, CancellationToken ct)
    {
        if (PartyIdFor(txn.Type, txn.FromAccountId, txn.ToAccountId) is not { } partyId)
        {
            return [];
        }

        var allowed = visibleTypes?.ToArray() ?? Enum.GetValues<TransactionType>();
        return await db.Transactions.AsNoTracking()
            .Where(t => t.CompanyId == companyId && t.Id != txn.Id && !t.IsDeleted
                && t.Status == TransactionStatus.Posted && t.Date == txn.Date && t.Amount == txn.Amount
                && (t.FromAccountId == partyId || t.ToAccountId == partyId) && allowed.Contains(t.Type))
            .OrderByDescending(t => t.CreatedAtUtc).ThenBy(t => t.Id)
            .ToListAsync(ct);
    }

    private static Guid? PartyIdFor(TransactionType type, Guid from, Guid to) => PostingRules.RolesFor(type) switch
    {
        (PostingRules.AccountRole.Party, _) => from,
        (_, PostingRules.AccountRole.Party) => to,
        _ => null,
    };

    private static async Task<LedgerDto> BuildLedgerDtoAsync(
        SqliteDbContext db, Guid companyId, AccountLedger ledger, IReadOnlySet<TransactionType> visibleTxnTypes, CancellationToken ct)
    {
        var txnIds = ledger.Lines.Select(l => l.Transaction.Id).ToList();
        var attachments = txnIds.Count > 0
            ? await db.Attachments.AsNoTracking()
                .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.TransactionId != null && txnIds.Contains(a.TransactionId.Value))
                .OrderBy(a => a.CreatedAtUtc).ThenBy(a => a.Id)
                .ToListAsync(ct)
            : [];
        var byTxn = attachments.ToLookup(a => a.TransactionId!.Value);

        return new LedgerDto(
            ledger.Account.Id,
            Balances.SignedOpening(ledger.Account),
            ledger.Account.OpeningAsOf,
            ledger.Lines.Select(l => new LedgerLineDto(
                l.Transaction.Id,
                l.Transaction.Type.ToString(),
                l.Transaction.VoucherNo,
                l.Transaction.Date,
                l.Debit,
                l.Credit,
                l.Balance,
                ledger.OtherSide(l.Transaction),
                l.Transaction.Remarks,
                l.Transaction.ChequeNo,
                visibleTxnTypes.Contains(l.Transaction.Type)
                    ? byTxn[l.Transaction.Id].Select(ToAttachmentDto).ToList()
                    : [])).ToList(),
            ledger.ClosingBalance);
    }

    private string CompanyFilesFolder(Guid companyId) => Path.Combine(filesRoot, companyId.ToString("N"));

    private string? SafeFilePath(Guid companyId, string key) =>
        key.Length > 0 && key is not ("." or "..") && key == Path.GetFileName(key)
            ? Path.Combine(CompanyFilesFolder(companyId), key)
            : null;

    private void DeleteQuietly(Guid companyId, string? key)
    {
        if (key is null || SafeFilePath(companyId, key) is not { } path) return;
        TryDeleteFile(path);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static int MoneyTypeOrder(AccountType type) => type switch
    {
        AccountType.Cash => 0,
        AccountType.Bank => 1,
        AccountType.Wallet => 2,
        _ => 3,
    };

    private static string JoinErrors(IReadOnlyDictionary<string, string[]> errors) =>
        string.Join(" ", errors.Values.SelectMany(v => v));

    private static PartyDto ToPartyDto(Account p, decimal balance, IReadOnlyDictionary<Guid, string> categories) =>
        new(p.Id, p.Name, p.PartyType!.Value.ToString(), p.Phone, p.Email, p.Address, p.Pan,
            p.PartyCategoryId, p.PartyCategoryId is { } c ? categories.GetValueOrDefault(c) : null,
            p.OpeningBalance, MasterValidation.OpeningSideName(p.OpeningSide), p.OpeningAsOf, balance, p.CreatedAtUtc);

    private static MoneyAccountDto ToMoneyAccountDto(Account a, decimal balance) =>
        new(a.Id, a.Name, a.Type.ToString(), a.AccountHolderName, a.AccountNumber,
            Balances.SignedOpening(a), a.OpeningAsOf, a.IsSystem, balance);

    private static TransactionDto ToTransactionDto(Transaction t) => new(
        t.Id, t.CompanyId, t.Type.ToString(), t.VoucherNo, t.Date, t.Amount, t.FromAccountId, t.ToAccountId,
        t.Remarks, t.ChequeNo, t.ChequeDate, t.Status.ToString(), t.CreatedBy ?? Guid.Empty, t.CreatedAtUtc);

    private static AttachmentDto ToAttachmentDto(Attachment a) =>
        new(a.Id, a.TransactionId, a.AccountId, a.FileName, a.ContentType, a.SizeBytes, a.StorageKey, a.CreatedAtUtc);
}
