using System.Text.Json;
using LekhaBook.Api.Security;
using LekhaBook.Application;
using LekhaBook.Contracts;
using LekhaBook.Data.Postgres;
using LekhaBook.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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
        var member = await access.RequireMemberAsync();
        if (MasterValidation.Validate(request) is { Count: > 0 } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var type = MasterValidation.ParseTransactionType(request.Type)!.Value;

        // A retried create (same client-generated id) returns what the first attempt saved.
        if (request.Id is { } requestedId && await FindByIdAsync(db, requestedId) is { } existing)
        {
            return Replay(companyId, member, existing);
        }

        var zone = await TimeZoneAsync(db, companyId);
        var today = CompanyClock.Today(zone, clock);
        var date = request.Date ?? today;
        if (date > today)
        {
            return Invalid("date", "The date can't be in the future.");
        }

        if (EntryRules.CheckCreate(member, type, date, today) is { } denied)
        {
            return Forbidden(denied);
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
        if (await PostingProblemAsync(db, transaction) is { } problem)
        {
            return problem;
        }

        db.Transactions.Add(transaction);
        Audit(db, member, transaction, "Create", before: null, clock);
        try
        {
            await db.SaveWithVersionsAsync(companyId, [transaction], member.UserId, clock.GetUtcNow().UtcDateTime);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The same create arrived twice at once: the other request saved it first.
            db.ChangeTracker.Clear();
            if (await FindByIdAsync(db, id) is not { } saved)
            {
                throw;
            }

            return Replay(companyId, member, saved);
        }

        return TypedResults.Created($"/api/companies/{companyId}/transactions/{transaction.Id}", ToDto(transaction));
    }

    private static async Task<Results<Ok<TransactionDto>, NotFound, ValidationProblem, ProblemHttpResult>> UpdateTransaction(
        Guid companyId, Guid transactionId, UpdateTransactionRequest request, CompanyAccess access, PostgresDbContext db, TimeProvider clock)
    {
        var member = await access.RequireMemberAsync();
        var transaction = await FindAsync(db, companyId, transactionId, tracked: true);
        if (transaction is null || transaction.Status != TransactionStatus.Posted)
        {
            return TypedResults.NotFound();
        }

        if (MasterValidation.Validate(request) is { Count: > 0 } errors)
        {
            return TypedResults.ValidationProblem(errors);
        }

        if (request.Type is not null && MasterValidation.ParseTransactionType(request.Type) != transaction.Type)
        {
            return Invalid("type", "An entry's type can't be changed. Void it and add a new one.");
        }

        var zone = await TimeZoneAsync(db, companyId);
        var today = CompanyClock.Today(zone, clock);
        if (request.Date > today)
        {
            return Invalid("date", "The date can't be in the future.");
        }

        if (EntryRules.CheckChange(member, transaction, EntryAction.Edit, request.Date, today, zone) is { } denied)
        {
            return Forbidden(denied);
        }

        var before = Snapshot(transaction);
        transaction.Date = request.Date;
        transaction.Amount = request.Amount;
        transaction.FromAccountId = request.FromAccountId;
        transaction.ToAccountId = request.ToAccountId;
        transaction.Remarks = MasterValidation.Clean(request.Remarks);
        transaction.ChequeNo = MasterValidation.Clean(request.ChequeNo);
        transaction.ChequeDate = request.ChequeDate;
        if (await PostingProblemAsync(db, transaction) is { } problem)
        {
            return problem;
        }

        Audit(db, member, transaction, "Update", before, clock);
        await db.SaveWithVersionsAsync(companyId, [transaction], member.UserId, clock.GetUtcNow().UtcDateTime);
        return TypedResults.Ok(ToDto(transaction));
    }

    /// <summary>"Delete" voids: the row is kept, and balances and statements skip it (D15).</summary>
    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> VoidTransaction(
        Guid companyId, Guid transactionId, CompanyAccess access, PostgresDbContext db, TimeProvider clock)
    {
        var member = await access.RequireMemberAsync();
        var transaction = await FindAsync(db, companyId, transactionId, tracked: true);
        if (transaction is null)
        {
            return TypedResults.NotFound();
        }

        var zone = await TimeZoneAsync(db, companyId);
        var today = CompanyClock.Today(zone, clock);
        if (EntryRules.CheckChange(member, transaction, EntryAction.Delete, null, today, zone) is { } denied)
        {
            return Forbidden(denied);
        }

        if (transaction.Status == TransactionStatus.Void)
        {
            return TypedResults.NoContent(); // a retried void
        }

        var before = Snapshot(transaction);
        transaction.Status = TransactionStatus.Void;
        Audit(db, member, transaction, "Void", before, clock);
        await db.SaveWithVersionsAsync(companyId, [transaction], member.UserId, clock.GetUtcNow().UtcDateTime);
        return TypedResults.NoContent();
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

        var storageKey = $"{Guid.CreateVersion7()}{Path.GetExtension(file.FileName)}";
        var folder = Path.Combine(AppContext.BaseDirectory, "attachments", companyId.ToString());
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
        await db.SaveWithVersionsAsync(companyId, [attachment], member.UserId, clock.GetUtcNow().UtcDateTime);
        return TypedResults.Created($"/api/companies/{companyId}/attachments/{attachment.Id}", ToDto(attachment));
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

        var storagePath = Path.Combine(AppContext.BaseDirectory, "attachments", companyId.ToString(), attachment.StorageKey ?? attachment.Id.ToString());
        return File.Exists(storagePath)
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
        return TypedResults.Ok(attachments.Select(ToDto).ToList());
    }

    // ---------------- Helpers ----------------

    private static Task<Transaction?> FindAsync(PostgresDbContext db, Guid companyId, Guid id, bool tracked)
    {
        var query = tracked ? db.Transactions : db.Transactions.AsNoTracking();
        return query.SingleOrDefaultAsync(t => t.Id == id && t.CompanyId == companyId && !t.IsDeleted);
    }

    /// <summary>By id across companies: only to tell a retry from an id that is already taken.</summary>
    private static Task<Transaction?> FindByIdAsync(PostgresDbContext db, Guid id) =>
        db.Transactions.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id);

    private static Results<Created<TransactionDto>, Ok<TransactionDto>, ValidationProblem, ProblemHttpResult> Replay(
        Guid companyId, CompanyMember member, Transaction existing)
    {
        if (existing.CompanyId != companyId || existing.IsDeleted || existing.CreatedBy != member.UserId)
        {
            return Conflict("That id is already in use.");
        }

        return TypedResults.Ok(ToDto(existing));
    }

    /// <summary>
    /// Loads both accounts <em>from this company only</em> and applies the posting rules, so an unknown or
    /// another company's account is a bad request.
    /// </summary>
    private static async Task<ValidationProblem?> PostingProblemAsync(PostgresDbContext db, Transaction transaction)
    {
        var ids = new[] { transaction.FromAccountId, transaction.ToAccountId };
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => a.CompanyId == transaction.CompanyId && !a.IsDeleted && ids.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id);

        if (!accounts.TryGetValue(transaction.FromAccountId, out var from))
        {
            return Invalid("fromAccountId", "That account doesn't exist.");
        }

        if (!accounts.TryGetValue(transaction.ToAccountId, out var to))
        {
            return Invalid("toAccountId", "That account doesn't exist.");
        }

        var errors = PostingRules.Validate(transaction, from, to);
        return errors.Count == 0
            ? null
            : TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["transaction"] = [.. errors] });
    }

    private static Task<string> TimeZoneAsync(PostgresDbContext db, Guid companyId) =>
        db.Companies.Where(c => c.Id == companyId).Select(c => c.TimeZoneId).SingleAsync();

    private static TransactionDto ToDto(Transaction t) => new(
        t.Id, t.CompanyId, t.Type.ToString(), t.VoucherNo, t.Date, t.Amount, t.FromAccountId, t.ToAccountId,
        t.Remarks, t.ChequeNo, t.ChequeDate, t.Status.ToString(), t.CreatedBy ?? Guid.Empty, t.CreatedAtUtc);

    private static AttachmentDto ToDto(Attachment a) =>
        new(a.Id, a.TransactionId, a.AccountId, a.FileName, a.ContentType, a.SizeBytes, a.StorageKey, a.CreatedAtUtc);

    private static string Snapshot(Transaction t) => JsonSerializer.Serialize(new
    {
        Type = t.Type.ToString(), t.VoucherNo, t.Date, t.Amount, t.FromAccountId, t.ToAccountId,
        t.Remarks, t.ChequeNo, t.ChequeDate, Status = t.Status.ToString(),
    });

    private static void Audit(
        PostgresDbContext db, CompanyMember member, Transaction transaction, string action, string? before, TimeProvider clock) =>
        db.AuditLogs.Add(new AuditLog
        {
            CompanyId = transaction.CompanyId,
            EntityType = nameof(Transaction),
            EntityId = transaction.Id,
            Action = action,
            BeforeJson = before,
            AfterJson = Snapshot(transaction),
            UserId = member.UserId,
            AtUtc = clock.GetUtcNow().UtcDateTime,
        });

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static ProblemHttpResult Forbidden(string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status403Forbidden);

    private static ProblemHttpResult Conflict(string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status409Conflict);
}
