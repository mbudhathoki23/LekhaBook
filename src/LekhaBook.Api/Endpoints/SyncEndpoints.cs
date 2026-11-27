using System.Text.Json;
using System.Text.Json.Serialization;
using LekhaBook.Api.Security;
using LekhaBook.Contracts;
using LekhaBook.Data.Postgres;
using LekhaBook.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace LekhaBook.Api.Endpoints;

/// <summary>
/// Device sync (AGENT_PLAN § 6, D17). Push goes through <see cref="BooksWriter"/>, so a device can't do anything
/// an online save couldn't. A refused item is reported inside a 200 response: devices treat HTTP 403 as "you were
/// removed from this company" and wipe its data, so only the membership check may return it.
/// </summary>
public static class SyncEndpoints
{
    private const int MaxPushItems = 200;
    private const int MaxPullRows = 1000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static void MapSyncEndpoints(this IEndpointRouteBuilder app)
    {
        var company = app.MapGroup("/api/companies/{companyId:guid}/sync").RequireAuthorization().RequireCompanyMember();

        company.MapPost("/push", Push);
        company.MapGet("/pull", Pull);
    }

    // ---------------- Push ----------------

    private static async Task<Results<Ok<PushResponse>, ValidationProblem>> Push(
        Guid companyId, PushRequest request, CompanyAccess access, PostgresDbContext db, TimeProvider clock, ILoggerFactory logs)
    {
        if (request.Items is null || request.Items.Count > MaxPushItems)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["items"] = [$"Send at most {MaxPushItems} items."] });
        }

        var writer = new BooksWriter(db, clock, companyId, await access.RequireMemberAsync());
        var results = new List<PushItemResult>(request.Items.Count);

        // In order, each saved on its own: one refused item must not undo or smuggle in another.
        foreach (var item in request.Items)
        {
            db.ChangeTracker.Clear();
            try
            {
                results.Add(Result(item, await ApplyAsync(writer, item)));
            }
            catch (JsonException)
            {
                results.Add(Refused(item, "The change couldn't be read."));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logs.CreateLogger("LekhaBook.Sync").LogError(ex, "Push item failed for company {CompanyId}", companyId);
                results.Add(Refused(item, "The server couldn't save this change."));
            }
        }

        return TypedResults.Ok(new PushResponse(results));
    }

    private static async Task<WriteResult> ApplyAsync(BooksWriter writer, PushItem? item)
    {
        switch (item?.EntityType, item?.Operation)
        {
            case ("Transaction", "Create"):
                var create = Read<CreateTransactionRequest>(item);
                // Without its own id a retried push would add the entry twice.
                return create.Id is null || create.Id == Guid.Empty
                    ? WriteResult.Invalid("id", "A new entry needs an id.")
                    : await writer.CreateTransactionAsync(create);

            case ("Transaction", "Update"):
                var update = Read<SyncUpdatePayload>(item);
                return update.Entry is null
                    ? WriteResult.Invalid("entry", "The change is missing.")
                    : await writer.UpdateTransactionAsync(update.Id, update.Entry, item.BaseVersion);

            case ("Transaction", "Void"):
                return await writer.VoidTransactionAsync(Read<SyncVoidPayload>(item).Id, item.BaseVersion);

            case ("PartyCategory", "Create"):
                var category = Read<SavePartyCategoryRequest>(item);
                return category.Id is null || category.Id == Guid.Empty
                    ? WriteResult.Invalid("id", "A new category needs an id.")
                    : await writer.CreateCategoryAsync(category);

            case ("Party", "Create"):
                var partyCreate = Read<SavePartyRequest>(item);
                return partyCreate.Id is null || partyCreate.Id == Guid.Empty
                    ? WriteResult.Invalid("id", "A new party needs an id.")
                    : await writer.CreatePartyAsync(partyCreate);

            case ("Party", "Update"):
                var partyUpdate = Read<SyncPartyUpdatePayload>(item);
                return partyUpdate.Party is null
                    ? WriteResult.Invalid("party", "The change is missing.")
                    : await writer.UpdatePartyAsync(partyUpdate.Id, partyUpdate.Party, item.BaseVersion);

            case ("Party", "Delete"):
                return await writer.DeletePartyAsync(Read<SyncDeletePayload>(item).Id, item.BaseVersion);

            case ("MoneyAccount", "Create"):
                var accountCreate = Read<SaveMoneyAccountRequest>(item);
                return accountCreate.Id is null || accountCreate.Id == Guid.Empty
                    ? WriteResult.Invalid("id", "A new account needs an id.")
                    : await writer.CreateMoneyAccountAsync(accountCreate);

            case ("MoneyAccount", "Update"):
                var accountUpdate = Read<SyncMoneyAccountUpdatePayload>(item);
                return accountUpdate.Account is null
                    ? WriteResult.Invalid("account", "The change is missing.")
                    : await writer.UpdateMoneyAccountAsync(accountUpdate.Id, accountUpdate.Account, item.BaseVersion);

            case ("MoneyAccount", "Delete"):
                return await writer.DeleteMoneyAccountAsync(Read<SyncDeletePayload>(item).Id, item.BaseVersion);

            case ("Category", "Create"):
                var head = Read<SaveCategoryRequest>(item);
                return head.Id == Guid.Empty
                    ? WriteResult.Invalid("id", "A new category needs an id.")
                    : await writer.CreateHeadCategoryAsync(head);

            case ("Adjustment", "Create"):
                var adj = Read<SyncAdjustmentPayload>(item);
                return adj.Adjustment is null || adj.Adjustment.Id == Guid.Empty
                    ? WriteResult.Invalid("id", "An adjustment needs an id.")
                    : await writer.CreateAdjustmentAsync(adj.AccountId, adj.Adjustment);

            case ("Attachment", "Delete"):
                return await writer.DeleteAttachmentAsync(Read<SyncDeletePayload>(item).Id);

            default:
                // Attachments in particular: their rows come only from the upload endpoint, which names the file.
                return WriteResult.Invalid("entityType", "This kind of change can't be synced.");
        }
    }

    private static T Read<T>(PushItem item)
        where T : class =>
        JsonSerializer.Deserialize<T>(item.Payload ?? "null", Json) ?? throw new JsonException("Empty payload.");

    private static PushItemResult Result(PushItem? item, WriteResult write)
    {
        // The device looks for "Conflict" to keep the change for review.
        var error = write.Ok ? null : write.Status == WriteStatus.Conflict ? $"Conflict: {write.Error}" : write.Error;
        return new PushItemResult(item?.IdempotencyKey ?? string.Empty, write.Ok, error, FinalVoucherNo: null, write.Row?.Version);
    }

    private static PushItemResult Refused(PushItem? item, string error) =>
        new(item?.IdempotencyKey ?? string.Empty, Ok: false, error, FinalVoucherNo: null, Version: null);

    // ---------------- Pull ----------------

    /// <summary>
    /// Every member gets the whole company's rows: balances computed on a device must be complete (plan § 4.2).
    /// </summary>
    private static async Task<Ok<PullResponse>> Pull(Guid companyId, PostgresDbContext db, long since = 0, int take = 500)
    {
        take = Math.Clamp(take, 1, MaxPullRows);

        // Read the latest version first. Versions follow commit order, so everything up to it is visible below;
        // a write that commits meanwhile has a higher one and is left for the next pull.
        var latest = await db.CompanySyncStates.AsNoTracking()
            .Where(s => s.CompanyId == companyId)
            .Select(s => s.LastVersion)
            .SingleOrDefaultAsync();

        var rows = new List<PulledRow>();
        await AddAsync(rows, db.Accounts, "Account", companyId, since, latest, take);
        await AddAsync(rows, db.PartyCategories, "PartyCategory", companyId, since, latest, take);
        await AddAsync(rows, db.Transactions, "Transaction", companyId, since, latest, take);
        await AddAsync(rows, db.Attachments, "Attachment", companyId, since, latest, take);

        // One page across all tables, so the cursor never passes a row that wasn't sent.
        var page = rows.OrderBy(r => r.Version).Take(take).ToList();
        var cursor = page.Count == take ? page[^1].Version : Math.Max(latest, since);
        return TypedResults.Ok(new PullResponse(cursor, page));
    }

    private static async Task AddAsync<T>(
        List<PulledRow> rows, IQueryable<T> table, string name, Guid companyId, long since, long latest, int take)
        where T : SyncEntity
    {
        var changed = await table.AsNoTracking()
            .Where(x => x.CompanyId == companyId && x.Version > since && x.Version <= latest)
            .OrderBy(x => x.Version)
            .Take(take)
            .ToListAsync();

        rows.AddRange(changed.Select(x => new PulledRow(
            name, x.Id, x.Version, x.UpdatedAtUtc, x.UpdatedBy, x.UpdatedOnDevice, x.IsDeleted, JsonSerializer.Serialize(x))));
    }
}
