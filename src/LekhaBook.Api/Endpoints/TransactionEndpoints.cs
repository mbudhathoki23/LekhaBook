using LekhaBook.Api.Security;
using LekhaBook.Application;
using LekhaBook.Contracts;
using LekhaBook.Data.Postgres;
using LekhaBook.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace LekhaBook.Api.Endpoints;

/// <summary>
/// Transactions and their attachments (AGENT_PLAN § 5.2, § 7, D15, D16). The access policy only proves
/// membership; each handler checks the permission for the entry's type, and every lookup is filtered by the
/// route's company. <see cref="PostingRules"/> decides which accounts fit, <see cref="EntryRules"/> who may
/// write. Every write is stamped with a server version, and every change to a transaction is audited.
/// </summary>
public static class TransactionEndpoints
{
    public static void MapTransactionEndpoints(this IEndpointRouteBuilder app)
    {
        var company = app.MapGroup("/api/companies/{companyId:guid}").RequireAuthorization().RequireCompanyMember();

        company.MapGet("/categories", ListCategories).RequirePermission(Permission.IncomeExpenseView);

        company.MapGet("/transactions", ListTransactions);
        company.MapGet("/transactions/{transactionId:guid}", GetTransaction);
        company.MapPost("/transactions", CreateTransaction);
        company.MapPut("/transactions/{transactionId:guid}", UpdateTransaction);
        company.MapDelete("/transactions/{transactionId:guid}", VoidTransaction);

        company.MapPost("/attachments", CreateAttachment);
        company.MapGet("/attachments/{id:guid}", GetAttachment);
        company.MapGet("/attachments", ListAttachments);
        company.MapDelete("/attachments/{id:guid}", DeleteAttachment);
    }

    // ---------------- Income and expense heads ----------------

    private static async Task<Ok<List<CategoryDto>>> ListCategories(Guid companyId, PostgresDbContext db)
    {
        var heads = await db.Accounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.IsActive
                        && (a.Type == AccountType.Income || a.Type == AccountType.Expense))
            .OrderBy(a => a.Name)
            .ToListAsync();
        return TypedResults.Ok(heads.Select(a => new CategoryDto(a.Id, a.Name, a.Type.ToString())).ToList());
    }

    // ---------------- Transactions ----------------

    private static async Task<Ok<List<TransactionDto>>> ListTransactions(
        Guid companyId, CompanyAccess access, PostgresDbContext db, Guid? accountId, DateOnly? fromDate, DateOnly? toDate)
    {
        var member = await access.RequireMemberAsync();
        var visible = Enum.GetValues<TransactionType>().Where(t => member.Has(EntryRules.ViewPermissionFor(t))).ToList();

        var query = db.Transactions.AsNoTracking()
            .Where(t => t.CompanyId == companyId && !t.IsDeleted && t.Status == TransactionStatus.Posted && visible.Contains(t.Type));
        if (accountId is { } account)
        {
            query = query.Where(t => t.FromAccountId == account || t.ToAccountId == account);
        }

        if (fromDate is { } from)
        {
            query = query.Where(t => t.Date >= from);
        }

        if (toDate is { } to)
        {
            query = query.Where(t => t.Date <= to);
        }

        var transactions = await query.OrderByDescending(t => t.Date).ThenByDescending(t => t.CreatedAtUtc).ToListAsync();
        return TypedResults.Ok(transactions.Select(ToDto).ToList());
    }

    private static async Task<Results<Ok<TransactionDto>, NotFound, ProblemHttpResult>> GetTransaction(
        Guid companyId, Guid transactionId, CompanyAccess access, PostgresDbContext db)
    {
        var member = await access.RequireMemberAsync();
        var transaction = await FindAsync(db, companyId, transactionId, tracked: false);
        if (transaction is null)
        {
            return TypedResults.NotFound();
        }

        return member.Has(EntryRules.ViewPermissionFor(transaction.Type))
            ? TypedResults.Ok(ToDto(transaction))
            : Forbidden("You don't have permission to view this entry.");
    }

    private static async Task<Results<Created<TransactionDto>, Ok<TransactionDto>, ValidationProblem, ProblemHttpResult>> CreateTransaction(
        Guid companyId, CreateTransactionRequest request, CompanyAccess access, PostgresDbContext db, TimeProvider clock)
    {
        var writer = new BooksWriter(db, clock, companyId, await access.RequireMemberAsync());
        var result = await writer.CreateTransactionAsync(request);
        return result.Status switch
        {
            WriteStatus.Created => TypedResults.Created(
                $"/api/companies/{companyId}/transactions/{result.Row!.Id}", ToDto((Transaction)result.Row)),
            WriteStatus.Replayed => TypedResults.Ok(ToDto((Transaction)result.Row!)),
            WriteStatus.PossibleDuplicate => DuplicateWarning(result.Duplicates!),
            WriteStatus.Invalid => Invalid(result),
            WriteStatus.Forbidden => Forbidden(result.Error!),
            _ => Conflict(result.Error!),
        };
    }

    private static async Task<Results<Ok<TransactionDto>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateTransaction(
        Guid companyId, Guid transactionId, UpdateTransactionRequest request, CompanyAccess access, PostgresDbContext db, TimeProvider clock)
    {
        var writer = new BooksWriter(db, clock, companyId, await access.RequireMemberAsync());
        var result = await writer.UpdateTransactionAsync(transactionId, request, expectedVersion: null);
        return result.Status switch
        {
            WriteStatus.Saved => TypedResults.Ok(ToDto((Transaction)result.Row!)),
            WriteStatus.PossibleDuplicate => DuplicateWarning(result.Duplicates!),
            WriteStatus.NotFound => TypedResults.NotFound(),
            WriteStatus.Invalid => Invalid(result),
            WriteStatus.Forbidden => Forbidden(result.Error!),
            _ => Conflict(result.Error!),
        };
    }

    private static ProblemHttpResult DuplicateWarning(IReadOnlyList<Transaction> matches) =>
        TypedResults.Problem(
            type: DuplicateTransactionWarning.ProblemType,
            title: "Possible duplicate entry",
            detail: "A posted entry already has this party, date and amount.",
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?>
            {
                ["duplicateWarning"] = new DuplicateTransactionWarning(matches.Select(ToDto).ToList()),
            });

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> VoidTransaction(
        Guid companyId, Guid transactionId, CompanyAccess access, PostgresDbContext db, TimeProvider clock)
    {
        var writer = new BooksWriter(db, clock, companyId, await access.RequireMemberAsync());
        var result = await writer.VoidTransactionAsync(transactionId, expectedVersion: null);
        return result.Status switch
        {
            WriteStatus.Saved or WriteStatus.Replayed => TypedResults.NoContent(),
            WriteStatus.NotFound => TypedResults.NotFound(),
            WriteStatus.Forbidden => Forbidden(result.Error!),
            _ => Conflict(result.Error!),
        };
    }

    // ---------------- Attachments (AGENT_PLAN § 7) ----------------

    private static async Task<Results<Created<AttachmentDto>, ValidationProblem, ProblemHttpResult>> CreateAttachment(
        Guid companyId, HttpRequest request, CompanyAccess access, PostgresDbContext db, TimeProvider clock)
    {
        var member = await access.RequireMemberAsync();

        if (!request.HasFormContentType)
        {
            return Invalid("file", "Expected multipart form data.");
        }

        var form = await request.ReadFormAsync();
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0)
        {
            return Invalid("file", "A file is required.");
        }

        var allowedTypes = new[] { "image/jpeg", "image/png", "image/webp", "application/pdf" };
        if (!allowedTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
        {
            return Invalid("file", "File type not allowed. Use JPEG, PNG, WebP or PDF.");
        }

        if (file.Length > 5 * 1024 * 1024)
        {
            return Invalid("file", "File too large. Maximum 5 MB.");
        }

        if (!Guid.TryParse(form["transactionId"].ToString(), out var transactionId)
            || await FindAsync(db, companyId, transactionId, tracked: false) is not { Status: TransactionStatus.Posted } transaction)
        {
            return Invalid("transactionId", "Transaction not found.");
        }

        // Adding proof changes the entry, so it needs the same right as editing it.
        var zone = await TimeZoneAsync(db, companyId);
        var today = CompanyClock.Today(zone, clock);
        if (EntryRules.CheckChange(member, transaction, EntryAction.Edit, null, today, zone) is { } denied)
        {
            return Forbidden(denied);
        }

        // Checked before the file is written, and again under the company's version lock below, so two quick uploads can't both pass.
        if ((await AttachmentCount(db, companyId, transactionId)) >= FileStore.MaxAttachmentsPerEntry)
        {
            return Invalid("file", AttachmentLimitMessage);
        }

        var storageKey = FileStore.NewKey(Path.GetExtension(file.FileName));
        var folder = FileStore.CompanyFolder(companyId);
        Directory.CreateDirectory(folder);
        await using (var stream = File.Create(Path.Combine(folder, storageKey)))
        {
            await file.CopyToAsync(stream);
        }

        var attachment = new Attachment
        {
            CompanyId = companyId,
            TransactionId = transactionId,
            FileName = file.FileName,
            ContentType = file.ContentType,
            SizeBytes = file.Length,
            StorageKey = storageKey,
        };

        db.Attachments.Add(attachment);
        var saved = await db.SaveWithVersionsAsync(companyId, [attachment], member.UserId, clock.GetUtcNow().UtcDateTime,
            stillValid: async () => (await AttachmentCount(db, companyId, transactionId)) < FileStore.MaxAttachmentsPerEntry);
        if (!saved)
        {
            FileStore.DeleteQuietly(companyId, storageKey);
            return Invalid("file", AttachmentLimitMessage);
        }

        return TypedResults.Created($"/api/companies/{companyId}/attachments/{attachment.Id}", FileStore.ToDto(attachment));
    }

    private static async Task<IResult> GetAttachment(Guid companyId, Guid id, CompanyAccess access, PostgresDbContext db)
    {
        var member = await access.RequireMemberAsync();
        var attachment = await db.Attachments.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == id && a.CompanyId == companyId && !a.IsDeleted);
        if (attachment?.TransactionId is not { } transactionId
            || await FindAsync(db, companyId, transactionId, tracked: false) is not { } transaction)
        {
            return TypedResults.NotFound();
        }

        if (!member.Has(EntryRules.ViewPermissionFor(transaction.Type)))
        {
            return Forbidden("You don't have permission to view this entry.");
        }

        var storagePath = FileStore.StoragePath(companyId, attachment.StorageKey ?? attachment.Id.ToString());
        return storagePath is not null && File.Exists(storagePath)
            ? TypedResults.File(await File.ReadAllBytesAsync(storagePath), attachment.ContentType, attachment.FileName)
            : TypedResults.NotFound();
    }

    private static async Task<Results<Ok<List<AttachmentDto>>, NotFound, ProblemHttpResult>> ListAttachments(
        Guid companyId, CompanyAccess access, PostgresDbContext db, Guid transactionId)
    {
        var member = await access.RequireMemberAsync();
        if (await FindAsync(db, companyId, transactionId, tracked: false) is not { } transaction)
        {
            return TypedResults.NotFound();
        }

        if (!member.Has(EntryRules.ViewPermissionFor(transaction.Type)))
        {
            return Forbidden("You don't have permission to view this entry.");
        }

        var attachments = await db.Attachments.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.TransactionId == transactionId && !a.IsDeleted)
            .OrderByDescending(a => a.CreatedAtUtc)
            .ToListAsync();
        return TypedResults.Ok(attachments.Select(FileStore.ToDto).ToList());
    }

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteAttachment(
        Guid companyId, Guid id, CompanyAccess access, PostgresDbContext db, TimeProvider clock)
    {
        var member = await access.RequireMemberAsync();
        // Tracked, so the soft delete and its new version are saved.
        var attachment = await db.Attachments.SingleOrDefaultAsync(a => a.Id == id && a.CompanyId == companyId && !a.IsDeleted);
        if (attachment?.TransactionId is not { } transactionId
            || await FindAsync(db, companyId, transactionId, tracked: false) is not { } transaction)
        {
            return TypedResults.NotFound();
        }

        // Removing proof changes the entry, so it needs the same right as adding it.
        var zone = await TimeZoneAsync(db, companyId);
        var today = CompanyClock.Today(zone, clock);
        if (EntryRules.CheckChange(member, transaction, EntryAction.Edit, null, today, zone) is { } denied)
        {
            return Forbidden(denied);
        }

        // Soft delete only: the file stays on disk, so nothing that still holds the key is left with a dangling file.
        attachment.IsDeleted = true;
        await db.SaveWithVersionsAsync(companyId, [attachment], member.UserId, clock.GetUtcNow().UtcDateTime);
        return TypedResults.NoContent();
    }

    // ---------------- Helpers ----------------

    private static Task<Transaction?> FindAsync(PostgresDbContext db, Guid companyId, Guid id, bool tracked)
    {
        var query = tracked ? db.Transactions : db.Transactions.AsNoTracking();
        return query.SingleOrDefaultAsync(t => t.Id == id && t.CompanyId == companyId && !t.IsDeleted);
    }

    private const string AttachmentLimitMessage = "An entry can have at most 5 attachments.";

    private static Task<int> AttachmentCount(PostgresDbContext db, Guid companyId, Guid transactionId) =>
        db.Attachments.CountAsync(a => a.CompanyId == companyId && a.TransactionId == transactionId && !a.IsDeleted);

    private static Task<string> TimeZoneAsync(PostgresDbContext db, Guid companyId) =>
        db.Companies.Where(c => c.Id == companyId).Select(c => c.TimeZoneId).SingleAsync();

    private static TransactionDto ToDto(Transaction t) => new(
        t.Id, t.CompanyId, t.Type.ToString(), t.VoucherNo, t.Date, t.Amount, t.FromAccountId, t.ToAccountId,
        t.Remarks, t.ChequeNo, t.ChequeDate, t.Status.ToString(), t.CreatedBy ?? Guid.Empty, t.CreatedAtUtc);

    private static ValidationProblem Invalid(WriteResult result) =>
        TypedResults.ValidationProblem(result.Errors!.ToDictionary());

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static ProblemHttpResult Forbidden(string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status403Forbidden);

    private static ProblemHttpResult Conflict(string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status409Conflict);
}
