using LekhaBook.Application;
using LekhaBook.Contracts;
using LekhaBook.Data.Sqlite;
using LekhaBook.Domain;
using LekhaBook.UI.Services;

namespace LekhaBook.Offline;

/// <summary>
/// The server's permission rules, applied on the device before a change is saved, so that offline nobody is shown
/// something as saved that the server will refuse. The server still decides at upload. The user's rights are those
/// last received from it (D11); in offline-only mode the user owns the books.
/// <para>Each method returns why not, or null. Input that is merely invalid is left to the normal checks.</para>
/// </summary>
internal sealed class DeviceRules(ISessionService session, LocalBooksStore store, TimeProvider clock)
{
    private const string NotAMember = "You are not a member of this company.";

    private static readonly IReadOnlySet<PartyType> AllParties = Enum.GetValues<PartyType>().ToHashSet();

    private static readonly IReadOnlySet<TransactionType> AllTypes = Enum.GetValues<TransactionType>().ToHashSet();

    public string? Has(Guid companyId, string permission, string denied) =>
        Member(companyId) is not { } member ? NotAMember : member.Has(permission) ? null : denied;

    // ---------------- Parties and money accounts ----------------

    public async Task<string?> AddPartyAsync(Guid companyId, SavePartyRequest request)
    {
        if (Member(companyId) is not { } member)
        {
            return NotAMember;
        }

        if (MasterValidation.ParsePartyType(request.PartyType) is not { } type)
        {
            return null;
        }

        var today = await TodayAsync(companyId);
        return MasterRules.CheckCreateParty(member, type, request.OpeningBalance, request.OpeningAsOf ?? today, today);
    }

    public async Task<string?> EditPartyAsync(Guid companyId, Guid partyId, SavePartyRequest request)
    {
        if (Member(companyId) is not { } member)
        {
            return NotAMember;
        }

        if (MasterValidation.ParsePartyType(request.PartyType) is not { } type
            || (await store.GetPartyAsync(companyId, partyId, AllParties)).Value is not { } party)
        {
            return null;
        }

        var today = await TodayAsync(companyId);
        var changed = party.OpeningBalance != request.OpeningBalance
            || !string.Equals(party.OpeningSide, request.OpeningSide, StringComparison.OrdinalIgnoreCase);
        var asOf = request.OpeningAsOf ?? (changed ? today : party.OpeningAsOf ?? today);
        return MasterRules.CheckEditParty(member, Enum.Parse<PartyType>(party.PartyType), type,
            changed || party.OpeningAsOf != asOf, request.OpeningBalance, asOf, today);
    }

    public async Task<string?> DeletePartyAsync(Guid companyId, Guid partyId) =>
        Member(companyId) is not { } member ? NotAMember
        : (await store.GetPartyAsync(companyId, partyId, AllParties)).Value is { } party
            ? MasterRules.CheckDeleteParty(member, Enum.Parse<PartyType>(party.PartyType))
            : null;

    public async Task<string?> AddMoneyAccountAsync(Guid companyId, SaveMoneyAccountRequest request)
    {
        if (Member(companyId) is not { } member)
        {
            return NotAMember;
        }

        if (!member.Has(Permission.AccountsAdd))
        {
            return "You don't have permission to add accounts.";
        }

        var today = await TodayAsync(companyId);
        return MasterRules.CheckMoneyAccountOpening(member, request.OpeningBalance, request.OpeningAsOf ?? today, today);
    }

    public async Task<string?> EditMoneyAccountAsync(Guid companyId, Guid accountId, SaveMoneyAccountRequest request)
    {
        if (Member(companyId) is not { } member)
        {
            return NotAMember;
        }

        if (!member.Has(Permission.AccountsEdit))
        {
            return "You don't have permission to edit accounts.";
        }

        if ((await store.GetMoneyAccountAsync(companyId, accountId)).Value is not { } account)
        {
            return null;
        }

        var today = await TodayAsync(companyId);
        var changed = account.OpeningBalance != request.OpeningBalance;
        var asOf = request.OpeningAsOf ?? (changed ? today : account.OpeningAsOf ?? today);
        return changed || account.OpeningAsOf != asOf
            ? MasterRules.CheckMoneyAccountOpening(member, request.OpeningBalance, asOf, today)
            : null;
    }

    // ---------------- Entries ----------------

    public async Task<string?> AddEntryAsync(Guid companyId, TransactionType? type, DateOnly? date)
    {
        if (Member(companyId) is not { } member)
        {
            return NotAMember;
        }

        if (type is not { } entryType)
        {
            return null;
        }

        var today = await TodayAsync(companyId);
        return EntryRules.CheckCreate(member, entryType, date ?? today, today);
    }

    public async Task<string?> ChangeEntryAsync(Guid companyId, Guid entryId, EntryAction action, DateOnly? newDate = null)
    {
        if (Member(companyId) is not { } member)
        {
            return NotAMember;
        }

        if ((await store.GetTransactionAsync(companyId, entryId, AllTypes)).Value is not { } entry)
        {
            return null;
        }

        var existing = new Transaction
        {
            Id = entry.Id,
            CompanyId = companyId,
            VoucherNo = entry.VoucherNo,
            Type = Enum.Parse<TransactionType>(entry.Type),
            Date = entry.Date,
            CreatedBy = entry.CreatedBy,
            CreatedAtUtc = entry.CreatedAtUtc,
        };
        return EntryRules.CheckChange(member, existing, action, newDate, await TodayAsync(companyId), await ZoneAsync(companyId));
    }

    // ---------------- Helpers ----------------

    private CompanyMember? Member(Guid companyId)
    {
        var userId = session.Me?.UserId ?? UserDatabaseFactory.LocalOfflineUserId;
        if (session.IsLocalMode)
        {
            return new CompanyMember { CompanyId = companyId, UserId = userId, DisplayName = "Owner", Role = CompanyRole.Owner };
        }

        if (session.Me?.Companies.FirstOrDefault(c => c.CompanyId == companyId) is not { } company)
        {
            return null;
        }

        return new CompanyMember
        {
            CompanyId = companyId,
            UserId = userId,
            DisplayName = session.UserName ?? "",
            Role = Enum.TryParse<CompanyRole>(company.Role, out var role) ? role : CompanyRole.EntryPerson,
            CustomPermissions = [.. company.Permissions],
        };
    }

    private async Task<string> ZoneAsync(Guid companyId) =>
        (await store.GetCompanyDetailsAsync(companyId)).Value?.TimeZoneId is { Length: > 0 } zone
            ? zone
            : CompanyClock.DefaultTimeZoneId;

    private async Task<DateOnly> TodayAsync(Guid companyId) => CompanyClock.Today(await ZoneAsync(companyId), clock);
}
