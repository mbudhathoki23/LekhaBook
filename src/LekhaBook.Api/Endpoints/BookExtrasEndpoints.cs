using System.Text.Json;
using LekhaBook.Api.Security;
using LekhaBook.Contracts;
using LekhaBook.Data;
using LekhaBook.Data.Postgres;
using LekhaBook.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace LekhaBook.Api.Endpoints;

/// <summary>
/// Balance adjustments (AGENT_PLAN § 5.2: Add/Reduce Money and party Adjust Balance) and new expense or income heads
/// (§ 5.1). Adjustments post through <see cref="BooksWriter"/>, so the same posting and entry rules apply; the server
/// picks the entry type and the system Adjustment account, which clients never see.
/// </summary>
public static class BookExtrasEndpoints
{
    private const int CategoryNameMax = 100;

    public static void MapBookExtrasEndpoints(this IEndpointRouteBuilder app)
    {
        var company = app.MapGroup("/api/companies/{companyId:guid}").RequireAuthorization().RequireCompanyMember();
        company.MapPost("/accounts/{accountId:guid}/adjustments", Adjust);
        company.MapPost("/categories", CreateCategory).RequirePermission(Permission.SettingsFeatures);
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem, ProblemHttpResult>> Adjust(
        Guid companyId, Guid accountId, AdjustBalanceRequest request, CompanyAccess access, PostgresDbContext db, TimeProvider clock)
    {
        var member = await access.RequireMemberAsync();
        var account = await db.Accounts.AsNoTracking()
            .SingleOrDefaultAsync(a => a.Id == accountId && a.CompanyId == companyId && !a.IsDeleted);
        if (account is null || !(account.Type == AccountType.Party || account.IsMoneyAccount))
        {
            return TypedResults.NotFound();
        }

        // The same rights as seeing the balance being changed.
        var view = account.Type == AccountType.Party
            ? MasterRules.PartyViewPermission(account.PartyType ?? PartyType.Customer)
            : Permission.AccountsView;
        if (!member.Has(view))
        {
            return Forbidden("You don't have permission to view this account.");
        }

        var system = await db.Accounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.Type == AccountType.System)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync();
        if (system is not { } adjustment)
        {
            return Conflict("This company has no adjustment account.");
        }

        var type = account.Type == AccountType.Party
            ? request.Increase ? TransactionType.PartyAdjustAdd : TransactionType.PartyAdjustReduce
            : request.Increase ? TransactionType.AddMoney : TransactionType.ReduceMoney;
        // "To" is debited, which raises a balance (PostingRules).
        var (from, to) = request.Increase ? (adjustment, account.Id) : (account.Id, adjustment);

        var writer = new BooksWriter(db, clock, companyId, member);
        var result = await writer.CreateTransactionAsync(new CreateTransactionRequest(
            type.ToString(), request.Date, request.Amount, from, to, request.Remarks, null, null, request.Id));
        return result.Status switch
        {
            WriteStatus.Created or WriteStatus.Replayed => TypedResults.NoContent(),
            WriteStatus.Invalid => TypedResults.ValidationProblem(result.Errors!.ToDictionary()),
            WriteStatus.Forbidden => Forbidden(result.Error!),
            _ => Conflict(result.Error ?? "The adjustment couldn't be saved."),
        };
    }

    private static async Task<Results<Created<CategoryDto>, Ok<CategoryDto>, ValidationProblem, ProblemHttpResult>> CreateCategory(
        Guid companyId, SaveCategoryRequest request, CompanyAccess access, PostgresDbContext db, TimeProvider clock)
    {
        var member = await access.RequireMemberAsync();
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > CategoryNameMax)
        {
            return Invalid("name", $"Enter a name of up to {CategoryNameMax} characters.");
        }

        AccountType? type = request.Type switch
        {
            nameof(AccountType.Expense) => AccountType.Expense,
            nameof(AccountType.Income) => AccountType.Income,
            _ => null,
        };
        if (type is not { } headType)
        {
            return Invalid("type", "Choose Expense or Income.");
        }

        // A retried create returns what the first attempt saved.
        if (await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == request.Id) is { } existing)
        {
            return existing.CompanyId == companyId && existing.Type == headType && !existing.IsDeleted
                ? TypedResults.Ok(new CategoryDto(existing.Id, existing.Name, existing.Type.ToString()))
                : Conflict("That id is already in use.");
        }

        var names = await db.Accounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.Type == headType)
            .Select(a => a.Name)
            .ToListAsync();
        if (names.Contains(name, StringComparer.CurrentCultureIgnoreCase))
        {
            return Conflict("There is already a category with that name.");
        }

        var account = new Account { Id = request.Id, CompanyId = companyId, Name = name, Type = headType };
        db.Accounts.Add(account);
        db.AuditLogs.Add(new AuditLog
        {
            CompanyId = companyId,
            EntityType = nameof(Account),
            EntityId = account.Id,
            Action = "Create",
            AfterJson = JsonSerializer.Serialize(new { account.Name, Type = headType.ToString() }),
            UserId = member.UserId,
            AtUtc = clock.GetUtcNow().UtcDateTime,
        });
        await db.SaveWithVersionsAsync(companyId, [account], member.UserId, clock.GetUtcNow().UtcDateTime);

        var dto = new CategoryDto(account.Id, account.Name, headType.ToString());
        return TypedResults.Created($"/api/companies/{companyId}/categories/{account.Id}", dto);
    }

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private static ProblemHttpResult Forbidden(string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status403Forbidden);

    private static ProblemHttpResult Conflict(string detail) =>
        TypedResults.Problem(detail, statusCode: StatusCodes.Status409Conflict);
}
