namespace LekhaBook.Domain;

/// <summary>The company's local calendar date (Nepal is UTC+5:45, so "today" is not the UTC date).</summary>
public static class CompanyClock
{
    public const string DefaultTimeZoneId = "Asia/Kathmandu";

    public static DateOnly Today(string timeZoneId, TimeProvider clock) => LocalDate(timeZoneId, clock.GetUtcNow().UtcDateTime);

    public static DateOnly LocalDate(string timeZoneId, DateTime utc)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        return DateOnly.FromDateTime(local);
    }
}

public enum EntryAction
{
    Create,
    Edit,
    Delete,
}

/// <summary>
/// Who may create, edit or delete a transaction (AGENT_PLAN § 4.2). Pure rules: the server runs them
/// on every write; the UI uses them only to hide buttons.
/// </summary>
public static class EntryRules
{
    /// <summary>
    /// The grant needed to see an entry of <paramref name="type"/>. Money movements follow account viewing; a party
    /// adjustment has no view grant of its own, so it follows managing adjustments.
    /// </summary>
    public static string ViewPermissionFor(TransactionType type) => type switch
    {
        TransactionType.PaymentIn => Permission.PaymentInView,
        TransactionType.PaymentOut => Permission.PaymentOutView,
        TransactionType.Expense or TransactionType.OtherIncome => Permission.IncomeExpenseView,
        TransactionType.AddMoney or TransactionType.ReduceMoney or TransactionType.TransferMoney => Permission.AccountsView,
        TransactionType.PartyAdjustAdd or TransactionType.PartyAdjustReduce => Permission.PartyAdjustmentsManage,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>The grant needed for <paramref name="action"/> on an entry of <paramref name="type"/>.</summary>
    public static string PermissionFor(TransactionType type, EntryAction action) => (type, action) switch
    {
        (TransactionType.PaymentIn, EntryAction.Create) => Permission.PaymentInCreate,
        (TransactionType.PaymentIn, EntryAction.Edit) => Permission.PaymentInEdit,
        (TransactionType.PaymentIn, EntryAction.Delete) => Permission.PaymentInDelete,
        (TransactionType.PaymentOut, EntryAction.Create) => Permission.PaymentOutCreate,
        (TransactionType.PaymentOut, EntryAction.Edit) => Permission.PaymentOutEdit,
        (TransactionType.PaymentOut, EntryAction.Delete) => Permission.PaymentOutDelete,
        (TransactionType.Expense or TransactionType.OtherIncome, EntryAction.Create) => Permission.IncomeExpenseCreate,
        (TransactionType.Expense or TransactionType.OtherIncome, EntryAction.Edit) => Permission.IncomeExpenseEdit,
        (TransactionType.Expense or TransactionType.OtherIncome, EntryAction.Delete) => Permission.IncomeExpenseDelete,
        (TransactionType.AddMoney or TransactionType.ReduceMoney or TransactionType.TransferMoney, EntryAction.Create) => Permission.AccountsAdjust,
        (TransactionType.AddMoney or TransactionType.ReduceMoney or TransactionType.TransferMoney, _) => Permission.AccountsAdjustEdit,
        (TransactionType.PartyAdjustAdd or TransactionType.PartyAdjustReduce, _) => Permission.PartyAdjustmentsManage,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>
    /// Checks a create. <paramref name="entryDate"/> is the date typed on the form;
    /// <paramref name="companyToday"/> comes from <see cref="CompanyClock"/>.
    /// </summary>
    public static string? CheckCreate(CompanyMember member, TransactionType type, DateOnly entryDate, DateOnly companyToday)
    {
        if (!member.IsActive)
        {
            return "Your access to this company has been removed.";
        }

        if (!member.Has(PermissionFor(type, EntryAction.Create)))
        {
            return $"You don't have permission to add {type} entries.";
        }

        if (member.Has(Permission.RestrictLockDate) && entryDate != companyToday)
        {
            return "You can only add entries dated today.";
        }

        return null;
    }

    /// <summary>
    /// Checks an edit or delete of <paramref name="existing"/>. For an edit, <paramref name="newDate"/> is the
    /// date after the change. "Same day" uses the server's <see cref="SyncEntity.CreatedAtUtc"/>, never the device clock.
    /// </summary>
    public static string? CheckChange(
        CompanyMember member, Transaction existing, EntryAction action, DateOnly? newDate, DateOnly companyToday, string timeZoneId)
    {
        if (action == EntryAction.Create)
        {
            throw new ArgumentException("Use CheckCreate for new entries.", nameof(action));
        }

        if (!member.IsActive)
        {
            return "Your access to this company has been removed.";
        }

        if (!member.Has(PermissionFor(existing.Type, action)))
        {
            return $"You don't have permission to {action.ToString().ToLowerInvariant()} {existing.Type} entries.";
        }

        if (existing.CreatedBy != member.UserId && !member.Has(Permission.EntriesEditOthers))
        {
            return "You can only change entries you added yourself.";
        }

        if (member.Has(Permission.RestrictSameDayEdits)
            && CompanyClock.LocalDate(timeZoneId, existing.CreatedAtUtc) != companyToday)
        {
            return "You can only change entries on the day you added them.";
        }

        if (member.Has(Permission.RestrictLockDate)
            && (existing.Date != companyToday || (newDate is { } d && d != companyToday)))
        {
            return "You can only change entries dated today.";
        }

        return null;
    }
}

/// <summary>
/// Rules for managing staff. Nobody can hand out more than they have, and the Owner can't be
/// changed or removed through staff management.
/// </summary>
public static class StaffRules
{
    /// <summary>Checks inviting someone, or changing a member's role/permissions, to the given access.</summary>
    public static string? CheckGrant(CompanyMember actor, CompanyRole role, IReadOnlyCollection<string>? customPermissions, bool isNewMember)
    {
        if (!actor.Has(isNewMember ? Permission.StaffCreate : Permission.StaffEdit))
        {
            return "You don't have permission to manage staff.";
        }

        if (role == CompanyRole.Owner)
        {
            return "A company has exactly one Owner; ownership transfer isn't available yet.";
        }

        if (customPermissions?.FirstOrDefault(p => !Permission.IsKnown(p)) is { } unknown)
        {
            return $"Unknown permission '{unknown}'.";
        }

        var granted = customPermissions is null
            ? RoleTemplates.For(role)
            : new HashSet<string>(customPermissions, StringComparer.Ordinal);
        var actorHas = actor.EffectivePermissions;

        // Grants: you can't give what you don't have.
        if (granted.Where(p => !Permission.IsRestriction(p)).FirstOrDefault(p => !actorHas.Contains(p)) is { } extra)
        {
            return $"You can't grant '{extra}' because you don't have it.";
        }

        // Restrictions: you can't lift a restriction that applies to you.
        if (Permission.AllRestrictions.FirstOrDefault(r => actorHas.Contains(r) && !granted.Contains(r)) is { } lifted)
        {
            return $"You can't remove the restriction '{lifted}' because it applies to you.";
        }

        return null;
    }

    /// <summary>Checks changing or removing <paramref name="target"/>.</summary>
    public static string? CheckManage(CompanyMember actor, CompanyMember target, bool revoke)
    {
        if (!actor.Has(revoke ? Permission.StaffDelete : Permission.StaffEdit))
        {
            return "You don't have permission to manage staff.";
        }

        if (target.CompanyId != actor.CompanyId)
        {
            return "That person isn't in this company.";
        }

        if (target.Role == CompanyRole.Owner)
        {
            return "The Owner can't be changed or removed.";
        }

        if (target.UserId == actor.UserId)
        {
            return "You can't change your own access.";
        }

        // No lateral escalation: you can only manage someone whose access is within yours.
        var actorHas = actor.EffectivePermissions;
        var targetHas = target.EffectivePermissions;
        if (targetHas.Where(p => !Permission.IsRestriction(p)).FirstOrDefault(p => !actorHas.Contains(p)) is { } more)
        {
            return $"You can't manage this person because they have '{more}', which you don't.";
        }

        if (Permission.AllRestrictions.FirstOrDefault(r => actorHas.Contains(r) && !targetHas.Contains(r)) is { } freer)
        {
            return $"You can't manage this person because '{freer}' applies to you but not to them.";
        }

        return null;
    }
}
