using System.Security.Cryptography;
using LekhaBook.Api.Security;
using LekhaBook.Contracts;
using LekhaBook.Data.Postgres;
using LekhaBook.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace LekhaBook.Api.Endpoints;

/// <summary>Current user, companies, staff and invitations (AGENT_PLAN § 4, § 9).</summary>
public static class CompanyEndpoints
{
    public static void MapCompanyEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/me", GetMe);
        api.MapPut("/me", UpdateMe);
        api.MapPost("/companies", CreateCompany);
        api.MapPost("/companies/migrate", MigrateOfflineCompany);
        api.MapPost("/invitations/accept", AcceptInvitation);

        // Everything under a company: members only, checked before the request body is read.
        var company = api.MapGroup("/companies/{companyId:guid}").RequireCompanyMember();
        company.MapGet("", GetCompany);
        company.MapPut("/calendar", UpdateCalendar).RequirePermission(Permission.SettingsBusiness);
        company.MapGet("/members", ListMembers).RequirePermission(Permission.StaffView);
        company.MapPut("/members/{memberId:guid}", UpdateMember).RequirePermission(Permission.StaffEdit);
        company.MapDelete("/members/{memberId:guid}", RevokeMember).RequirePermission(Permission.StaffDelete);
        company.MapGet("/invitations", ListInvitations).RequirePermission(Permission.StaffView);
        company.MapPost("/invitations", Invite).RequirePermission(Permission.StaffCreate);
        company.MapDelete("/invitations/{invitationId:guid}", CancelInvitation).RequirePermission(Permission.StaffCreate);

        // The logo is readable by every member; only business settings can change it.
        company.MapGet("/logo", GetLogo);
        company.MapPut("/logo", SetLogo).RequirePermission(Permission.SettingsBusiness);
        company.MapDelete("/logo", DeleteLogo).RequirePermission(Permission.SettingsBusiness);
    }

    // ---------------- Current user ----------------

    private static async Task<Results<Ok<MeResponse>, UnauthorizedHttpResult>> GetMe(
        HttpContext http, AuthDbContext auth, PostgresDbContext db)
    {
        if (CompanyAccess.UserId(http.User) is not { } userId || await auth.Users.FindAsync(userId) is not { } user)
        {
            return TypedResults.Unauthorized();
        }

        var companies = await (
            from m in db.CompanyMembers
            join c in db.Companies on m.CompanyId equals c.Id
            where m.UserId == userId && m.Status == MemberStatus.Active && !c.IsDeleted
            orderby c.Name
            select new { m, c.Name, c.Calendar }).ToListAsync();

        return TypedResults.Ok(new MeResponse(
            user.Id, user.Email!, user.DisplayName,
            companies.Select(x => Summary(x.m, x.Name, x.Calendar)).ToList()));
    }

    private static async Task<Results<NoContent, ValidationProblem, UnauthorizedHttpResult>> UpdateMe(
        UpdateMeRequest request, HttpContext http, AuthDbContext auth)
    {
        if (CompanyAccess.UserId(http.User) is not { } userId || await auth.Users.FindAsync(userId) is not { } user)
        {
            return TypedResults.Unauthorized();
        }

        if (Invalid(request.DisplayName, 200, "displayName") is { } problem)
        {
            return problem;
        }

        user.DisplayName = request.DisplayName.Trim();
        await auth.SaveChangesAsync();
        return TypedResults.NoContent();
    }

    // ---------------- Companies ----------------

    private static async Task<Results<Created<CompanySummary>, ValidationProblem, UnauthorizedHttpResult>> CreateCompany(
        CreateCompanyRequest request, HttpContext http, AuthDbContext auth, PostgresDbContext db, TimeProvider clock)
    {
        if (CompanyAccess.UserId(http.User) is not { } userId || await auth.Users.FindAsync(userId) is not { } user)
        {
            return TypedResults.Unauthorized();
        }

        if (Invalid(request.Name, 200, "name") is { } problem)
        {
            return problem;
        }

        var calendar = CalendarMode.BS;
        if (request.Calendar is not null)
        {
            if (ParseCalendar(request.Calendar) is not { } chosen)
            {
                return CalendarProblem();
            }

            calendar = chosen;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var company = new Company
        {
            Name = request.Name.Trim(),
            Calendar = calendar,
            Phone = request.Phone?.Trim(),
            Address = request.Address?.Trim(),
            Pan = request.Pan?.Trim(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var owner = new CompanyMember
        {
            CompanyId = company.Id,
            UserId = userId,
            DisplayName = user.DisplayName ?? user.Email!,
            Role = CompanyRole.Owner,
            JoinedAtUtc = now,
            UpdatedAtUtc = now,
        };

        // A brand-new company: its counter row is created here, so versions 1..n can be assigned directly.
        var accounts = CompanySeeder.DefaultAccounts(company.Id, now, userId);
        long version = 0;
        foreach (var account in accounts)
        {
            account.Version = ++version;
        }

        company.Version = ++version;
        db.Companies.Add(company);
        db.Accounts.AddRange(accounts);
        db.CompanySyncStates.Add(new CompanySyncState { CompanyId = company.Id, LastVersion = version });
        db.CompanyMembers.Add(owner);
        await db.SaveChangesAsync();

        return TypedResults.Created($"/api/companies/{company.Id}", Summary(owner, company.Name, company.Calendar));
    }

    /// <summary>
    /// Upgrades an offline local company into a synced online company, preserving client UUIDs so all account
    /// and transaction links stay intact. Idempotent by CompanyId so a retry after a network drop never duplicates data.
    /// </summary>
    private static async Task<Results<Created<MigrateOfflineCompanyResponse>, Ok<MigrateOfflineCompanyResponse>, ValidationProblem, ProblemHttpResult, UnauthorizedHttpResult>> MigrateOfflineCompany(
        MigrateOfflineCompanyRequest request, HttpContext http, AuthDbContext auth, PostgresDbContext db, TimeProvider clock)
    {
        if (CompanyAccess.UserId(http.User) is not { } userId || await auth.Users.FindAsync(userId) is not { } user)
        {
            return TypedResults.Unauthorized();
        }

        if (request.CompanyId == Guid.Empty)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["companyId"] = ["A company id is required."] });
        }

        if (Invalid(request.Name, 200, "name") is { } nameProblem)
        {
            return nameProblem;
        }

        var calendar = CalendarMode.BS;
        if (!string.IsNullOrWhiteSpace(request.Calendar))
        {
            if (ParseCalendar(request.Calendar) is not { } chosen)
            {
                return CalendarProblem();
            }

            calendar = chosen;
        }

        // Idempotent retry: if this exact company was already migrated by this user, return it cleanly.
        if (await db.Companies.AsNoTracking().SingleOrDefaultAsync(c => c.Id == request.CompanyId) is { } existingCompany)
        {
            var existingMember = await db.CompanyMembers.AsNoTracking()
                .SingleOrDefaultAsync(m => m.CompanyId == existingCompany.Id && m.UserId == userId && m.Status == MemberStatus.Active);
            if (existingMember is not { Role: CompanyRole.Owner })
            {
                return TypedResults.Problem("That company id is already in use.", statusCode: StatusCodes.Status409Conflict);
            }

            var existingAccounts = await db.Accounts.CountAsync(a => a.CompanyId == existingCompany.Id && !a.IsDeleted);
            var existingTxns = await db.Transactions.CountAsync(t => t.CompanyId == existingCompany.Id && !t.IsDeleted);
            return TypedResults.Ok(new MigrateOfflineCompanyResponse(
                Summary(existingMember, existingCompany.Name, existingCompany.Calendar),
                existingAccounts, existingTxns, AlreadyExisted: true));
        }

        var categoriesInput = request.PartyCategories ?? [];
        var accountsInput = request.Accounts ?? [];
        var txnsInput = request.Transactions ?? [];

        if (categoriesInput.Count > 500 || accountsInput.Count > 5000 || txnsInput.Count > 25000)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["company"] = ["Offline snapshot exceeds migration size limits."] });
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var zoneId = string.IsNullOrWhiteSpace(request.TimeZoneId) ? CompanyClock.DefaultTimeZoneId : request.TimeZoneId.Trim();
        var fyStart = request.FiscalYearStartMonth is >= 1 and <= 12 ? request.FiscalYearStartMonth : 4;
        var currency = string.IsNullOrWhiteSpace(request.Currency) || request.Currency.Trim().Length > 10 ? "NPR" : request.Currency.Trim();

        var company = new Company
        {
            Id = request.CompanyId,
            Name = request.Name.Trim(),
            Phone = request.Phone?.Trim(),
            Address = request.Address?.Trim(),
            Pan = request.Pan?.Trim(),
            Calendar = calendar,
            FiscalYearStartMonth = fyStart,
            Currency = currency,
            TimeZoneId = zoneId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };

        // Build & validate PartyCategories
        var seenCatIds = new HashSet<Guid>();
        var partyCategories = new List<PartyCategory>(categoriesInput.Count);
        foreach (var c in categoriesInput)
        {
            if (c.Id == Guid.Empty || !seenCatIds.Add(c.Id) || string.IsNullOrWhiteSpace(c.Name) || c.Name.Trim().Length > 100)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["partyCategories"] = ["Invalid party category in offline snapshot."] });
            }

            partyCategories.Add(new PartyCategory
            {
                Id = c.Id,
                CompanyId = company.Id,
                Name = c.Name.Trim(),
                IsDeleted = c.IsDeleted,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedBy = userId,
                UpdatedBy = userId,
            });
        }

        // Build & validate Accounts
        var accountMap = new Dictionary<Guid, Account>(accountsInput.Count);
        foreach (var a in accountsInput)
        {
            if (a.Id == Guid.Empty || accountMap.ContainsKey(a.Id) || string.IsNullOrWhiteSpace(a.Name) || a.Name.Trim().Length > 200)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["accounts"] = ["Invalid account in offline snapshot."] });
            }

            if (!Enum.TryParse<AccountType>(a.Type, ignoreCase: true, out var accType) || !Enum.IsDefined(accType))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["accounts"] = [$"Unknown account type '{a.Type}'."] });
            }

            if (!Enum.TryParse<BalanceSide>(a.OpeningSide, ignoreCase: true, out var openSide) || !Enum.IsDefined(openSide))
            {
                openSide = BalanceSide.Debit;
            }

            PartyType? partyType = null;
            if (accType == AccountType.Party)
            {
                if (!Enum.TryParse<PartyType>(a.PartyType, ignoreCase: true, out var parsedPt) || !Enum.IsDefined(parsedPt))
                {
                    return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["accounts"] = [$"Party '{a.Name}' needs a valid PartyType."] });
                }

                partyType = parsedPt;
            }

            if (a.OpeningBalance < 0 || decimal.Round(a.OpeningBalance, 2) != a.OpeningBalance)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["accounts"] = [$"Account '{a.Name}' has an invalid opening balance."] });
            }

            var catId = a.PartyCategoryId is { } cid && seenCatIds.Contains(cid) ? cid : (Guid?)null;
            var acc = new Account
            {
                Id = a.Id,
                CompanyId = company.Id,
                Name = a.Name.Trim(),
                Type = accType,
                IsSystem = a.IsSystem,
                IsActive = a.IsActive,
                OpeningBalance = a.OpeningBalance,
                OpeningSide = openSide,
                OpeningAsOf = a.OpeningAsOf,
                PartyType = partyType,
                Phone = a.Phone?.Trim(),
                Email = a.Email?.Trim(),
                Address = a.Address?.Trim(),
                Pan = a.Pan?.Trim(),
                PartyCategoryId = catId,
                AccountHolderName = a.AccountHolderName?.Trim(),
                AccountNumber = a.AccountNumber?.Trim(),
                IsDeleted = a.IsDeleted,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedBy = userId,
                UpdatedBy = userId,
            };
            accountMap[acc.Id] = acc;
        }

        // Ensure core seeded accounts exist if the offline snapshot had none (defensive).
        if (accountMap.Count == 0)
        {
            foreach (var seeded in CompanySeeder.DefaultAccounts(company.Id, now, userId))
            {
                accountMap[seeded.Id] = seeded;
            }
        }
        else if (!accountMap.Values.Any(a => a.Type == AccountType.System && !a.IsDeleted))
        {
            var adj = new Account
            {
                CompanyId = company.Id,
                Name = CompanySeeder.AdjustmentAccountName,
                Type = AccountType.System,
                IsSystem = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedBy = userId,
                UpdatedBy = userId,
            };
            accountMap[adj.Id] = adj;
        }

        // Build & validate Transactions against domain PostingRules
        var seenTxnIds = new HashSet<Guid>();
        var transactions = new List<Transaction>(txnsInput.Count);
        foreach (var t in txnsInput)
        {
            if (t.Id == Guid.Empty || !seenTxnIds.Add(t.Id))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["transactions"] = ["Duplicate or missing transaction id."] });
            }

            if (!Enum.TryParse<TransactionType>(t.Type, ignoreCase: true, out var txnType) || !Enum.IsDefined(txnType))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["transactions"] = [$"Unknown transaction type '{t.Type}'."] });
            }

            if (!Enum.TryParse<TransactionStatus>(t.Status, ignoreCase: true, out var status) || !Enum.IsDefined(status))
            {
                status = TransactionStatus.Posted;
            }

            if (!accountMap.TryGetValue(t.FromAccountId, out var from) || !accountMap.TryGetValue(t.ToAccountId, out var to))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["transactions"] = ["A transaction references an account not included in this company."] });
            }

            var entity = new Transaction
            {
                Id = t.Id,
                CompanyId = company.Id,
                Type = txnType,
                VoucherNo = string.IsNullOrWhiteSpace(t.VoucherNo) ? t.Id.ToString() : t.VoucherNo.Trim(),
                IsManualNo = t.IsManualNo,
                IsProvisionalNo = t.IsProvisionalNo,
                Date = t.Date,
                Amount = t.Amount,
                FromAccountId = t.FromAccountId,
                ToAccountId = t.ToAccountId,
                Remarks = t.Remarks?.Trim(),
                ChequeNo = t.ChequeNo?.Trim(),
                ChequeDate = t.ChequeDate,
                Status = status,
                IsDeleted = t.IsDeleted,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CreatedBy = userId,
                UpdatedBy = userId,
            };

            var postingErrors = PostingRules.Validate(entity, from, to);
            if (postingErrors.Count > 0)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["transactions"] = [.. postingErrors] });
            }

            transactions.Add(entity);
        }

        // Guard against any cross-company ID collisions before inserting.
        var accountIds = accountMap.Keys.ToList();
        var catIds = seenCatIds.ToList();
        var txnIds = seenTxnIds.ToList();
        if ((accountIds.Count > 0 && await db.Accounts.AnyAsync(a => accountIds.Contains(a.Id)))
            || (catIds.Count > 0 && await db.PartyCategories.AnyAsync(c => catIds.Contains(c.Id)))
            || (txnIds.Count > 0 && await db.Transactions.AnyAsync(t => txnIds.Contains(t.Id))))
        {
            return TypedResults.Problem("One or more record ids already exist on the server.", statusCode: StatusCodes.Status409Conflict);
        }

        // Stamp monotonic versions in commit order: Categories -> Accounts -> Transactions -> Company
        long version = 0;
        foreach (var c in partyCategories)
        {
            c.Version = ++version;
        }

        var allAccounts = accountMap.Values.ToList();
        foreach (var a in allAccounts)
        {
            a.Version = ++version;
        }

        foreach (var t in transactions)
        {
            t.Version = ++version;
        }

        company.Version = ++version;

        var owner = new CompanyMember
        {
            CompanyId = company.Id,
            UserId = userId,
            DisplayName = user.DisplayName ?? user.Email!,
            Role = CompanyRole.Owner,
            JoinedAtUtc = now,
            UpdatedAtUtc = now,
        };

        await using var tx = await db.Database.BeginTransactionAsync();
        db.Companies.Add(company);
        db.PartyCategories.AddRange(partyCategories);
        db.Accounts.AddRange(allAccounts);
        db.Transactions.AddRange(transactions);
        db.CompanySyncStates.Add(new CompanySyncState { CompanyId = company.Id, LastVersion = version });
        db.CompanyMembers.Add(owner);
        db.AuditLogs.Add(new AuditLog
        {
            CompanyId = company.Id,
            EntityType = nameof(Company),
            EntityId = company.Id,
            Action = "MigrateOffline",
            AfterJson = System.Text.Json.JsonSerializer.Serialize(new { company.Name, Accounts = allAccounts.Count, Transactions = transactions.Count }),
            UserId = userId,
            AtUtc = now,
        });
        await db.SaveChangesAsync();
        await tx.CommitAsync();

        var activeAccounts = allAccounts.Count(a => !a.IsDeleted);
        var activeTxns = transactions.Count(t => !t.IsDeleted);
        return TypedResults.Created(
            $"/api/companies/{company.Id}",
            new MigrateOfflineCompanyResponse(Summary(owner, company.Name, company.Calendar), activeAccounts, activeTxns, AlreadyExisted: false));
    }

    private static async Task<Results<Ok<CompanyDetails>, NotFound>> GetCompany(Guid companyId, PostgresDbContext db)
    {
        var c = await db.Companies.AsNoTracking().SingleOrDefaultAsync(x => x.Id == companyId && !x.IsDeleted);
        return c is null
            ? TypedResults.NotFound()
            : TypedResults.Ok(new CompanyDetails(
                c.Id, c.Name, c.Phone, c.Email, c.Address, c.Pan,
                c.Calendar.ToString(), c.Currency, c.TimeZoneId, c.FiscalYearStartMonth, HasLogo: c.LogoStorageKey != null));
    }

    /// <summary>Changes only how dates are shown and typed; stored dates are AD either way.</summary>
    private static async Task<Results<NoContent, NotFound, ValidationProblem>> UpdateCalendar(
        Guid companyId, UpdateCalendarRequest request, PostgresDbContext db, TimeProvider clock)
    {
        if (ParseCalendar(request.Calendar) is not { } calendar)
        {
            return CalendarProblem();
        }

        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == companyId && !c.IsDeleted);
        if (company is null)
        {
            return TypedResults.NotFound();
        }

        if (company.Calendar != calendar)
        {
            company.Calendar = calendar;
            company.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync();
        }

        return TypedResults.NoContent();
    }

    // ---------------- Logo ----------------

    private static async Task<Results<FileContentHttpResult, NotFound>> GetLogo(Guid companyId, PostgresDbContext db)
    {
        var company = await db.Companies.AsNoTracking().SingleOrDefaultAsync(c => c.Id == companyId && !c.IsDeleted);
        if (company?.LogoStorageKey is not { } key || company.LogoContentType is not { } contentType
            || FileStore.StoragePath(companyId, key) is not { } path || !File.Exists(path))
        {
            return TypedResults.NotFound();
        }

        return TypedResults.File(await File.ReadAllBytesAsync(path), contentType);
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> SetLogo(
        Guid companyId, HttpRequest request, PostgresDbContext db, TimeProvider clock)
    {
        if (!request.HasFormContentType)
        {
            return FileProblem("Expected multipart form data.");
        }

        var form = await request.ReadFormAsync();
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
        {
            return FileProblem("A file is required.");
        }

        if (!FileStore.LogoContentTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
        {
            return FileProblem("Logo must be a JPEG, PNG or WebP image.");
        }

        if (file.Length > FileStore.MaxLogoBytes)
        {
            return FileProblem("Logo too large. Maximum 2 MB.");
        }

        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == companyId && !c.IsDeleted);
        if (company is null)
        {
            return TypedResults.NotFound();
        }

        var contentType = file.ContentType.ToLowerInvariant();
        var key = FileStore.NewKey(FileStore.LogoExtension(contentType));
        var path = FileStore.StoragePath(companyId, key)
            ?? throw new InvalidOperationException("A generated file name must be a plain file name.");
        Directory.CreateDirectory(FileStore.CompanyFolder(companyId));
        await using (var stream = File.Create(path))
        {
            await file.CopyToAsync(stream);
        }

        // The new file is in place before the row points at it. The old file is removed only after the row is saved.
        var previous = company.LogoStorageKey;
        try
        {
            company.LogoStorageKey = key;
            company.LogoContentType = contentType;
            company.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync();
        }
        catch
        {
            FileStore.DeleteQuietly(companyId, key);
            throw;
        }

        FileStore.DeleteQuietly(companyId, previous);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound>> DeleteLogo(Guid companyId, PostgresDbContext db, TimeProvider clock)
    {
        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == companyId && !c.IsDeleted);
        if (company is null)
        {
            return TypedResults.NotFound();
        }

        if (company.LogoStorageKey is not { } previous)
        {
            return TypedResults.NoContent();
        }

        company.LogoStorageKey = null;
        company.LogoContentType = null;
        company.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();
        FileStore.DeleteQuietly(companyId, previous);
        return TypedResults.NoContent();
    }

    private static ValidationProblem FileProblem(string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [message] });

    // ---------------- Members ----------------

    private static async Task<Ok<List<MemberDto>>> ListMembers(Guid companyId, PostgresDbContext db)
    {
        var members = await db.CompanyMembers.AsNoTracking()
            .Where(m => m.CompanyId == companyId)
            .OrderBy(m => m.Role).ThenBy(m => m.DisplayName)
            .ToListAsync();
        return TypedResults.Ok(members.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<MemberDto>, NotFound, ProblemHttpResult, ValidationProblem>> UpdateMember(
        Guid companyId, Guid memberId, UpdateMemberRequest request, CompanyAccess access, PostgresDbContext db, TimeProvider clock)
    {
        var actor = await access.RequireMemberAsync();
        var target = await db.CompanyMembers.SingleOrDefaultAsync(m => m.Id == memberId && m.CompanyId == companyId);
        if (target is null)
        {
            return TypedResults.NotFound();
        }

        if (ParseRole(request.Role) is not { } role)
        {
            return RoleProblem();
        }

        if ((StaffRules.CheckManage(actor, target, revoke: false)
             ?? StaffRules.CheckGrant(actor, role, request.CustomPermissions, isNewMember: false)) is { } denied)
        {
            return Forbidden(denied);
        }

        target.Role = role;
        target.CustomPermissions = request.CustomPermissions?.Distinct().ToList();
        target.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();
        return TypedResults.Ok(ToDto(target));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> RevokeMember(
        Guid companyId, Guid memberId, CompanyAccess access, PostgresDbContext db, TimeProvider clock)
    {
        var actor = await access.RequireMemberAsync();
        var target = await db.CompanyMembers.SingleOrDefaultAsync(m => m.Id == memberId && m.CompanyId == companyId);
        if (target is null)
        {
            return TypedResults.NotFound();
        }

        if (StaffRules.CheckManage(actor, target, revoke: true) is { } denied)
        {
            return Forbidden(denied);
        }

        // The revoked user's sync requests now get 403, and their device wipes this company (phase 8).
        target.Status = MemberStatus.Revoked;
        target.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();
        return TypedResults.NoContent();
    }

    // ---------------- Invitations ----------------

    private static async Task<Ok<List<InvitationDto>>> ListInvitations(Guid companyId, PostgresDbContext db, TimeProvider clock)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var open = await db.Invitations.AsNoTracking()
            .Where(i => i.CompanyId == companyId && i.AcceptedAtUtc == null && i.CancelledAtUtc == null && i.ExpiresAtUtc > now)
            .OrderBy(i => i.CreatedAtUtc)
            .ToListAsync();
        return TypedResults.Ok(open.Select(i =>
            new InvitationDto(i.Id, i.NormalizedEmail.ToLowerInvariant(), i.DisplayName, i.Role.ToString(), i.ExpiresAtUtc)).ToList());
    }

    private static async Task<Results<Ok<InviteResponse>, ProblemHttpResult, ValidationProblem>> Invite(
        Guid companyId, InviteRequest request, CompanyAccess access, PostgresDbContext db, AuthDbContext auth, TimeProvider clock)
    {
        var actor = await access.RequireMemberAsync();

        if (Invalid(request.DisplayName, 200, "displayName") is { } nameProblem)
        {
            return nameProblem;
        }

        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@') || request.Email.Length > 256)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["A valid email is required."] });
        }

        if (ParseRole(request.Role) is not { } role)
        {
            return RoleProblem();
        }

        if (StaffRules.CheckGrant(actor, role, request.CustomPermissions, isNewMember: true) is { } denied)
        {
            return Forbidden(denied);
        }

        var email = Invitation.NormalizeEmail(request.Email);
        var existingUserId = await auth.Users.Where(u => u.NormalizedEmail == email).Select(u => (Guid?)u.Id).SingleOrDefaultAsync();
        if (existingUserId is { } uid
            && await db.CompanyMembers.AnyAsync(m => m.CompanyId == companyId && m.UserId == uid && m.Status == MemberStatus.Active))
        {
            return TypedResults.Problem("That person is already a member of this company.", statusCode: StatusCodes.Status409Conflict);
        }

        var token = Base64UrlToken();
        var now = clock.GetUtcNow().UtcDateTime;
        var invitation = new Invitation
        {
            CompanyId = companyId,
            NormalizedEmail = email,
            DisplayName = request.DisplayName.Trim(),
            Role = role,
            CustomPermissions = request.CustomPermissions?.Distinct().ToList(),
            TokenHash = Hash(token),
            InvitedBy = actor.UserId,
            CreatedAtUtc = now,
            ExpiresAtUtc = now + Invitation.Lifetime,
        };
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync();

        return TypedResults.Ok(new InviteResponse(invitation.Id, token, invitation.ExpiresAtUtc));
    }

    private static async Task<Results<NoContent, NotFound>> CancelInvitation(
        Guid companyId, Guid invitationId, PostgresDbContext db, TimeProvider clock)
    {
        var invitation = await db.Invitations.SingleOrDefaultAsync(i => i.Id == invitationId && i.CompanyId == companyId);
        if (invitation is null || invitation.AcceptedAtUtc is not null)
        {
            return TypedResults.NotFound();
        }

        invitation.CancelledAtUtc ??= clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<CompanySummary>, ProblemHttpResult, UnauthorizedHttpResult>> AcceptInvitation(
        AcceptInvitationRequest request, HttpContext http, AuthDbContext auth, PostgresDbContext db, TimeProvider clock)
    {
        if (CompanyAccess.UserId(http.User) is not { } userId || await auth.Users.FindAsync(userId) is not { } user)
        {
            return TypedResults.Unauthorized();
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var hash = Hash(request.Token ?? string.Empty);
        var invitation = await db.Invitations.SingleOrDefaultAsync(i => i.TokenHash == hash);
        if (invitation is null || !invitation.IsOpen(now))
        {
            return TypedResults.Problem("This invitation is invalid, expired or already used.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!string.Equals(user.NormalizedEmail, invitation.NormalizedEmail, StringComparison.Ordinal))
        {
            return Forbidden("This invitation was sent to a different email address.");
        }

        var member = await db.CompanyMembers.SingleOrDefaultAsync(m => m.CompanyId == invitation.CompanyId && m.UserId == userId);
        if (member is { IsActive: true })
        {
            return TypedResults.Problem("You are already a member of this company.", statusCode: StatusCodes.Status409Conflict);
        }

        if (member is null)
        {
            member = new CompanyMember { CompanyId = invitation.CompanyId, UserId = userId, DisplayName = invitation.DisplayName, JoinedAtUtc = now };
            db.CompanyMembers.Add(member);
        }

        // A previously revoked member comes back with the access in the new invitation.
        member.Role = invitation.Role;
        member.CustomPermissions = invitation.CustomPermissions;
        member.Status = MemberStatus.Active;
        member.DisplayName = invitation.DisplayName;
        member.UpdatedAtUtc = now;
        invitation.AcceptedAtUtc = now;
        invitation.AcceptedBy = userId;
        await db.SaveChangesAsync();

        var joined = await db.Companies.Where(c => c.Id == invitation.CompanyId).Select(c => new { c.Name, c.Calendar }).SingleAsync();
        return TypedResults.Ok(Summary(member, joined.Name, joined.Calendar));
    }

    // ---------------- Helpers ----------------

    private static CompanySummary Summary(CompanyMember m, string companyName, CalendarMode calendar) =>
        new(m.CompanyId, companyName, m.Role.ToString(), m.EffectivePermissions.Order(StringComparer.Ordinal).ToList(), calendar.ToString());

    private static CalendarMode? ParseCalendar(string? calendar) =>
        Enum.TryParse<CalendarMode>(calendar, ignoreCase: true, out var mode) && Enum.IsDefined(mode) ? mode : null;

    private static ValidationProblem CalendarProblem() =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["calendar"] = ["Calendar must be BS or AD."] });

    private static MemberDto ToDto(CompanyMember m) =>
        new(m.Id, m.UserId, m.DisplayName, m.Role.ToString(), m.Status.ToString(), m.CustomPermissions,
            m.EffectivePermissions.Order(StringComparer.Ordinal).ToList(), m.JoinedAtUtc);

    private static CompanyRole? ParseRole(string? role) =>
        Enum.TryParse<CompanyRole>(role, ignoreCase: true, out var r) && Enum.IsDefined(r) ? r : null;

    private static ValidationProblem RoleProblem() =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            ["role"] = [$"Role must be one of: {string.Join(", ", Enum.GetNames<CompanyRole>())}."],
        });

    private static ValidationProblem? Invalid(string? value, int maxLength, string field) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Length > maxLength
            ? TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [$"Required, at most {maxLength} characters."] })
            : null;

    private static ProblemHttpResult Forbidden(string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status403Forbidden);

    private static string Base64UrlToken() => System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
}
