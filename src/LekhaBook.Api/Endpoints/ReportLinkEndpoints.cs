using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using LekhaBook.Api.Security;
using LekhaBook.Contracts;
using LekhaBook.Data;
using LekhaBook.Data.Postgres;
using LekhaBook.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LekhaBook.Api.Endpoints;

/// <summary>
/// Public report links (decision D24). Members with export rights make a link to one statement; anyone holding it
/// reads that statement, with its files if the link allows, until it expires or is stopped. A link only works while
/// its creator could still make it. The token travels in a header, so it doesn't appear in request logs.
/// </summary>
public static class ReportLinkEndpoints
{
    public const string RateLimitPolicy = "public-reports";

    private static readonly string[] ServedFileTypes = ["image/jpeg", "image/png", "image/webp", "application/pdf"];

    public static void MapReportLinkEndpoints(this IEndpointRouteBuilder app)
    {
        var links = app.MapGroup("/api/companies/{companyId:guid}/report-links").RequireAuthorization().RequireCompanyMember();
        links.MapGet("/", ListLinks).RequirePermission(Permission.ReportsExport);
        links.MapPost("/", CreateLink).RequirePermission(Permission.ReportsExport);
        links.MapDelete("/{id:guid}", RevokeLink).RequirePermission(Permission.ReportsExport);

        // Signed in, but no company in the route: the link itself says which company, and membership is checked here.
        app.MapPost("/api/report-links/resolve", Resolve).RequireAuthorization();

        // Anonymous: the token is the key.
        var open = app.MapGroup("/api/public/report").RequireRateLimiting(RateLimitPolicy);
        open.MapGet("/", View);
        open.MapGet("/logo", Logo);
        open.MapGet("/files/{attachmentId:guid}", GetFile);
    }

    // ---------------- Managing links ----------------

    private static async Task<Results<Ok<List<ReportLinkDto>>, NotFound, ProblemHttpResult>> ListLinks(
        Guid companyId, Guid accountId, CompanyAccess access, PostgresDbContext db, TimeProvider clock)
    {
        var member = await access.RequireMemberAsync();
        var account = await FindAccountAsync(db, companyId, accountId);
        if (account is null)
        {
            return TypedResults.NotFound();
        }

        if (!ReportShareLink.MayShare(member, account))
        {
            return Forbidden();
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var live = await db.ReportShareLinks.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.AccountId == accountId && l.RevokedAtUtc == null && l.ExpiresAtUtc > now)
            .OrderByDescending(l => l.CreatedAtUtc)
            .ToListAsync();
        var zone = await TimeZoneAsync(db, companyId);
        return TypedResults.Ok(live.Select(l => ToDto(l, zone)).ToList());
    }

    private static async Task<Results<Ok<CreatedReportLinkDto>, NotFound, ValidationProblem, ProblemHttpResult>> CreateLink(
        Guid companyId, CreateReportLinkRequest request, CompanyAccess access, PostgresDbContext db,
        TimeProvider clock, IConfiguration configuration)
    {
        var member = await access.RequireMemberAsync();
        if (request.ExpiresInDays is < 1 or > ReportShareLink.MaxDays)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["expiresInDays"] = [$"Choose between 1 and {ReportShareLink.MaxDays} days."],
            });
        }

        var account = await FindAccountAsync(db, companyId, request.AccountId);
        if (account is null)
        {
            return TypedResults.NotFound();
        }

        if (!ReportShareLink.MayShare(member, account))
        {
            return Forbidden();
        }

        // Links carry their key in the address, so they must never point at a site nobody chose.
        if (PublicSite(configuration) is not { } site)
        {
            return TypedResults.Problem(
                "Sharing isn't set up yet: the server has no public web address.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var token = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var now = clock.GetUtcNow().UtcDateTime;
        var link = new ReportShareLink
        {
            CompanyId = companyId,
            AccountId = account.Id,
            TokenHash = Hash(token),
            IncludeFiles = request.IncludeFiles,
            CreatedByUserId = member.UserId,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(request.ExpiresInDays),
        };
        db.ReportShareLinks.Add(link);
        Audit(db, member, link, "Create", now);
        await db.SaveChangesAsync();

        return TypedResults.Ok(new CreatedReportLinkDto(ToDto(link, await TimeZoneAsync(db, companyId)), $"{site}/r/{token}", $"{site}/signin"));
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> RevokeLink(
        Guid companyId, Guid id, CompanyAccess access, PostgresDbContext db, TimeProvider clock)
    {
        var member = await access.RequireMemberAsync();
        var link = await db.ReportShareLinks.SingleOrDefaultAsync(l => l.Id == id && l.CompanyId == companyId);
        if (link is null)
        {
            return TypedResults.NotFound();
        }

        var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == link.AccountId && a.CompanyId == companyId);
        if (account is null || !ReportShareLink.MayShare(member, account))
        {
            return Forbidden();
        }

        if (link.RevokedAtUtc is null)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            link.RevokedAtUtc = now;
            Audit(db, member, link, "Revoke", now);
            await db.SaveChangesAsync();
        }

        return TypedResults.NoContent();
    }

    /// <summary>
    /// "Is this shared statement one of mine?" Answers only for an active member who may see it in the app;
    /// everyone else gets the same "not found" as for an unknown token.
    /// </summary>
    private static async Task<Results<Ok<ResolvedReportLinkDto>, NotFound>> Resolve(
        ResolveReportLinkRequest request, ClaimsPrincipal user, PostgresDbContext db, TimeProvider clock)
    {
        if (CompanyAccess.UserId(user) is not { } userId || await FindLinkAsync(db, request.Token, clock) is not { } shared)
        {
            return TypedResults.NotFound();
        }

        var member = await db.CompanyMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.CompanyId == shared.Link.CompanyId && m.UserId == userId);
        if (member is not { IsActive: true } || !member.Has(ReportShareLink.ViewPermission(shared.Account)))
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(new ResolvedReportLinkDto(
            shared.Link.CompanyId, shared.Account.Id, shared.Account.Type == AccountType.Party));
    }

    // ---------------- Reading a link ----------------

    private static async Task<Results<Ok<PublicReportDto>, NotFound>> View(
        [FromHeader(Name = ReportLinkHeaders.Token)] string? token, HttpContext http, PostgresDbContext db, TimeProvider clock)
    {
        NoStore(http);
        if (await FindLinkAsync(db, token, clock) is not { } shared
            || await db.GetLedgerAsync(shared.Link.CompanyId, shared.Account.Id) is not { } ledger)
        {
            return TypedResults.NotFound();
        }

        var files = (await SharedFilesAsync(db, shared, ledger.Lines.Select(l => l.Transaction).ToList()))
            .ToLookup(a => a.TransactionId!.Value);
        var company = shared.Company;
        var account = shared.Account;
        var isParty = account.Type == AccountType.Party;

        return TypedResults.Ok(new PublicReportDto(
            company.Name, company.Address, company.Phone, company.Pan, company.LogoStorageKey is not null,
            company.Calendar.ToString(),
            account.Name,
            isParty ? account.PartyType?.ToString() : null,
            isParty ? null : account.Type.ToString(),
            // A party's phone and email stay private: the link can be forwarded to anyone.
            isParty ? account.Address : null,
            isParty ? account.Pan : null,
            isParty ? null : account.AccountHolderName,
            isParty ? null : AccountNumbers.Mask(account.AccountNumber),
            CompanyClock.Today(company.TimeZoneId, clock),
            CompanyClock.LocalDate(company.TimeZoneId, shared.Link.ExpiresAtUtc),
            Balances.SignedOpening(account),
            account.OpeningAsOf,
            ledger.ClosingBalance,
            ledger.Lines.Select(l => new PublicReportLine(
                l.Transaction.Type.ToString(),
                Guid.TryParse(l.Transaction.VoucherNo, out _) ? string.Empty : l.Transaction.VoucherNo,
                l.Transaction.Date,
                l.Debit, l.Credit, l.Balance,
                ledger.OtherSide(l.Transaction),
                l.Transaction.Remarks,
                l.Transaction.ChequeNo,
                files[l.Transaction.Id].Select(a => new PublicReportFile(a.Id, a.FileName, a.ContentType)).ToList())).ToList()));
    }

    private static async Task<Results<FileContentHttpResult, NotFound>> Logo(
        [FromHeader(Name = ReportLinkHeaders.Token)] string? token, HttpContext http, PostgresDbContext db, TimeProvider clock)
    {
        NoStore(http);
        if (await FindLinkAsync(db, token, clock) is not { } shared
            || shared.Company.LogoStorageKey is not { } key
            || shared.Company.LogoContentType is not { } contentType
            || FileStore.StoragePath(shared.Company.Id, key) is not { } path
            || !File.Exists(path))
        {
            return TypedResults.NotFound();
        }

        return TypedResults.File(await File.ReadAllBytesAsync(path), contentType);
    }

    /// <summary>One file, only if it hangs on an entry that this very statement shows.</summary>
    private static async Task<Results<FileContentHttpResult, NotFound>> GetFile(
        [FromHeader(Name = ReportLinkHeaders.Token)] string? token, Guid attachmentId, HttpContext http,
        PostgresDbContext db, TimeProvider clock)
    {
        NoStore(http);
        if (await FindLinkAsync(db, token, clock) is not { } shared)
        {
            return TypedResults.NotFound();
        }

        var attachment = await db.Attachments.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == attachmentId && a.CompanyId == shared.Link.CompanyId && !a.IsDeleted);
        if (attachment?.TransactionId is not { } transactionId)
        {
            return TypedResults.NotFound();
        }

        var accountId = shared.Account.Id;
        var entry = await db.Transactions.AsNoTracking().SingleOrDefaultAsync(t =>
            t.Id == transactionId && t.CompanyId == shared.Link.CompanyId && !t.IsDeleted
            && t.Status == TransactionStatus.Posted && (t.FromAccountId == accountId || t.ToAccountId == accountId));
        if (entry is null
            || (await SharedFilesAsync(db, shared, [entry])).All(a => a.Id != attachmentId)
            || attachment.StorageKey is not { } key
            || FileStore.StoragePath(shared.Link.CompanyId, key) is not { } path
            || !File.Exists(path))
        {
            return TypedResults.NotFound();
        }

        http.Response.Headers.XContentTypeOptions = "nosniff";
        return TypedResults.File(await File.ReadAllBytesAsync(path), attachment.ContentType, attachment.FileName);
    }

    // ---------------- Helpers ----------------

    private sealed record SharedStatement(ReportShareLink Link, Company Company, Account Account, CompanyMember Creator);

    /// <summary>The live link for a token, or null. Unknown, expired and stopped links look the same to the caller.</summary>
    private static async Task<SharedStatement?> FindLinkAsync(PostgresDbContext db, string? token, TimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
        {
            return null;
        }

        var hash = Hash(token);
        var link = await db.ReportShareLinks.AsNoTracking().SingleOrDefaultAsync(l => l.TokenHash == hash);
        if (link is null || !link.IsLive(clock.GetUtcNow().UtcDateTime))
        {
            return null;
        }

        var company = await db.Companies.AsNoTracking().SingleOrDefaultAsync(c => c.Id == link.CompanyId && !c.IsDeleted);
        var account = await FindAccountAsync(db, link.CompanyId, link.AccountId);
        var creator = await db.CompanyMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.CompanyId == link.CompanyId && m.UserId == link.CreatedByUserId);

        return company is not null && account is not null && creator is not null && ReportShareLink.MayShare(creator, account)
            ? new SharedStatement(link, company, account, creator)
            : null;
    }

    /// <summary>The files a viewer of this link may see: none unless the link includes them, then what its creator may view.</summary>
    private static async Task<List<Attachment>> SharedFilesAsync(
        PostgresDbContext db, SharedStatement shared, IReadOnlyList<Transaction> entries)
    {
        if (!shared.Link.IncludeFiles)
        {
            return [];
        }

        var ids = entries.Where(t => shared.Creator.Has(EntryRules.ViewPermissionFor(t.Type))).Select(t => t.Id).ToList();
        var attachments = await db.Attachments.AsNoTracking()
            .Where(a => a.CompanyId == shared.Link.CompanyId && !a.IsDeleted && a.TransactionId != null && ids.Contains(a.TransactionId.Value))
            .OrderBy(a => a.CreatedAtUtc).ThenBy(a => a.Id)
            .ToListAsync();
        return attachments.Where(a => ServedFileTypes.Contains(a.ContentType, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    private static async Task<Account?> FindAccountAsync(PostgresDbContext db, Guid companyId, Guid accountId)
    {
        var account = await db.Accounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == accountId && a.CompanyId == companyId && !a.IsDeleted);
        return account is not null && ReportShareLink.CanShare(account) ? account : null;
    }

    /// <summary>The web app's own address from PublicSite:BaseUrl, or null when it isn't a usable one.</summary>
    private static string? PublicSite(IConfiguration configuration) =>
        Uri.TryCreate(configuration["PublicSite:BaseUrl"], UriKind.Absolute, out var url)
        && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
            ? url.AbsoluteUri.TrimEnd('/')
            : null;

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static Task<string> TimeZoneAsync(PostgresDbContext db, Guid companyId) =>
        db.Companies.Where(c => c.Id == companyId).Select(c => c.TimeZoneId).SingleAsync();

    private static ReportLinkDto ToDto(ReportShareLink l, string timeZoneId) =>
        new(l.Id, l.AccountId, l.IncludeFiles,
            CompanyClock.LocalDate(timeZoneId, l.CreatedAtUtc), CompanyClock.LocalDate(timeZoneId, l.ExpiresAtUtc));

    private static void NoStore(HttpContext http) => http.Response.Headers.CacheControl = "no-store";

    private static void Audit(PostgresDbContext db, CompanyMember member, ReportShareLink link, string action, DateTime now) =>
        db.AuditLogs.Add(new AuditLog
        {
            CompanyId = link.CompanyId,
            EntityType = nameof(ReportShareLink),
            EntityId = link.Id,
            Action = action,
            AfterJson = System.Text.Json.JsonSerializer.Serialize(new { link.AccountId, link.IncludeFiles, link.ExpiresAtUtc }),
            UserId = member.UserId,
            AtUtc = now,
        });

    private static ProblemHttpResult Forbidden() =>
        TypedResults.Problem("You don't have permission to share this statement.", statusCode: StatusCodes.Status403Forbidden);
}
