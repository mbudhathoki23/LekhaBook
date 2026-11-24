using LekhaBook.Domain;

namespace LekhaBook.Domain.Tests;

public sealed class AccessRulesTests
{
    private const string Ktm = CompanyClock.DefaultTimeZoneId;
    private static readonly Guid CompanyId = Guid.CreateVersion7();
    private static readonly DateOnly Today = new(2026, 11, 24);

    private static CompanyMember Member(CompanyRole role, List<string>? custom = null) => new()
    {
        CompanyId = CompanyId,
        UserId = Guid.CreateVersion7(),
        DisplayName = role.ToString(),
        Role = role,
        CustomPermissions = custom,
    };

    /// <summary>A Payment In created by <paramref name="author"/> at a Kathmandu local time on <paramref name="date"/>.</summary>
    private static Transaction Entry(CompanyMember author, DateOnly date, int localHour = 10) => new()
    {
        CompanyId = CompanyId,
        Type = TransactionType.PaymentIn,
        VoucherNo = "1",
        Date = date,
        Amount = 100m,
        CreatedBy = author.UserId,
        // Kathmandu is UTC+5:45.
        CreatedAtUtc = date.ToDateTime(new TimeOnly(localHour, 0), DateTimeKind.Utc).AddHours(-5).AddMinutes(-45),
    };

    // ---------- Company clock ----------

    [Theory]
    [InlineData("2026-11-24T18:14:00Z", "2026-11-24")]
    [InlineData("2026-11-24T18:15:00Z", "2026-11-25")]
    [InlineData("2026-11-23T18:15:00Z", "2026-11-24")]
    public void Kathmandu_date_changes_at_18_15_UTC(string utc, string expected)
    {
        var instant = DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal);

        Assert.Equal(DateOnly.Parse(expected), CompanyClock.LocalDate(Ktm, instant));
    }

    [Fact]
    public void Today_uses_the_injected_clock()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 24, 18, 30, 0, TimeSpan.Zero));

        Assert.Equal(new DateOnly(2026, 11, 25), CompanyClock.Today(Ktm, clock));
    }

    // ---------- Role templates ----------

    [Fact]
    public void Role_templates_follow_the_plan()
    {
        Assert.Equal(Permission.AllGrants, RoleTemplates.For(CompanyRole.Owner));
        Assert.DoesNotContain(Permission.BusinessDelete, RoleTemplates.For(CompanyRole.Partner));
        Assert.Contains(Permission.StaffCreate, RoleTemplates.For(CompanyRole.Partner));

        var manager = RoleTemplates.For(CompanyRole.Manager);
        Assert.DoesNotContain(manager, p => p.EndsWith(".delete", StringComparison.Ordinal));
        Assert.DoesNotContain(manager, p => p.StartsWith("staff.", StringComparison.Ordinal));
        Assert.DoesNotContain(Permission.AccountsAdd, manager);
        Assert.Contains(Permission.PaymentInEdit, manager);

        var accountant = RoleTemplates.For(CompanyRole.Accountant);
        Assert.DoesNotContain(accountant, p => p.StartsWith("staff.", StringComparison.Ordinal));
        Assert.Contains(Permission.SettingsFeatures, accountant);
        Assert.DoesNotContain(Permission.AccountsDelete, accountant);

        var entry = RoleTemplates.For(CompanyRole.EntryPerson);
        Assert.Contains(Permission.RestrictLockDate, entry);
        Assert.Contains(Permission.RestrictSameDayEdits, entry);
        Assert.Contains(Permission.ReportsParty, entry);
        Assert.DoesNotContain(Permission.EntriesEditOthers, entry);
        Assert.DoesNotContain(Permission.SettingsFeatures, entry);
        Assert.DoesNotContain(Permission.ReportsExport, entry);

        Assert.All(Enum.GetValues<CompanyRole>(), r => Assert.All(RoleTemplates.For(r), p => Assert.True(Permission.IsKnown(p), p)));
    }

    [Fact]
    public void Custom_permissions_replace_the_template_except_for_the_owner()
    {
        var partner = Member(CompanyRole.Partner, custom: [Permission.CustomersView]);
        Assert.True(partner.Has(Permission.CustomersView));
        Assert.False(partner.Has(Permission.StaffView));

        var owner = Member(CompanyRole.Owner, custom: [Permission.CustomersView]);
        Assert.True(owner.Has(Permission.StaffView));
        Assert.True(owner.Has(Permission.BusinessDelete));
    }

    [Fact]
    public void Revoked_member_has_nothing()
    {
        var partner = Member(CompanyRole.Partner);
        partner.Status = MemberStatus.Revoked;

        Assert.False(partner.Has(Permission.CustomersView));
        Assert.NotNull(EntryRules.CheckCreate(partner, TransactionType.PaymentIn, Today, Today));
    }

    // ---------- Entry rules ----------

    [Fact]
    public void Entry_person_can_only_add_entries_dated_today()
    {
        var clerk = Member(CompanyRole.EntryPerson);

        Assert.Null(EntryRules.CheckCreate(clerk, TransactionType.PaymentIn, Today, Today));
        Assert.Contains("dated today", EntryRules.CheckCreate(clerk, TransactionType.PaymentIn, Today.AddDays(-1), Today));
    }

    [Fact]
    public void Accountant_can_back_date()
    {
        var accountant = Member(CompanyRole.Accountant);

        Assert.Null(EntryRules.CheckCreate(accountant, TransactionType.Expense, Today.AddDays(-30), Today));
    }

    [Fact]
    public void Entry_person_edits_own_entry_the_same_day()
    {
        var clerk = Member(CompanyRole.EntryPerson);
        var own = Entry(clerk, Today);

        Assert.Null(EntryRules.CheckChange(clerk, own, EntryAction.Edit, Today, Today, Ktm));
        Assert.Null(EntryRules.CheckChange(clerk, own, EntryAction.Delete, null, Today, Ktm));
    }

    [Fact]
    public void Entry_person_cannot_change_someone_elses_entry()
    {
        var clerk = Member(CompanyRole.EntryPerson);
        var others = Entry(Member(CompanyRole.Accountant), Today);

        Assert.Contains("added yourself", EntryRules.CheckChange(clerk, others, EntryAction.Edit, Today, Today, Ktm));
    }

    [Fact]
    public void Entry_person_cannot_change_own_entry_the_next_day()
    {
        var clerk = Member(CompanyRole.EntryPerson);
        var yesterday = Entry(clerk, Today.AddDays(-1));

        Assert.Contains("on the day", EntryRules.CheckChange(clerk, yesterday, EntryAction.Edit, Today.AddDays(-1), Today, Ktm));
    }

    [Fact]
    public void Same_day_is_judged_in_company_time_not_utc()
    {
        var clerk = Member(CompanyRole.EntryPerson);
        // Created at 00:30 Kathmandu on the 24th = 18:45 UTC on the 23rd. Still "today" locally.
        var earlyMorning = Entry(clerk, Today, localHour: 0);
        earlyMorning.CreatedAtUtc = earlyMorning.CreatedAtUtc.AddMinutes(30);

        Assert.Equal(new DateOnly(2026, 11, 23), DateOnly.FromDateTime(earlyMorning.CreatedAtUtc));
        Assert.Null(EntryRules.CheckChange(clerk, earlyMorning, EntryAction.Edit, Today, Today, Ktm));
    }

    [Fact]
    public void Lock_date_blocks_moving_an_entry_to_another_day()
    {
        var clerk = Member(CompanyRole.EntryPerson);
        var own = Entry(clerk, Today);

        Assert.Contains("dated today", EntryRules.CheckChange(clerk, own, EntryAction.Edit, Today.AddDays(-2), Today, Ktm));
    }

    [Fact]
    public void Accountant_can_change_anyones_old_entries()
    {
        var accountant = Member(CompanyRole.Accountant);
        var old = Entry(Member(CompanyRole.EntryPerson), Today.AddDays(-40));

        Assert.Null(EntryRules.CheckChange(accountant, old, EntryAction.Edit, Today.AddDays(-41), Today, Ktm));
        Assert.Null(EntryRules.CheckChange(accountant, old, EntryAction.Delete, null, Today, Ktm));
    }

    [Fact]
    public void Manager_cannot_delete()
    {
        var manager = Member(CompanyRole.Manager);
        var entry = Entry(manager, Today);

        Assert.Null(EntryRules.CheckChange(manager, entry, EntryAction.Edit, Today, Today, Ktm));
        Assert.Contains("permission", EntryRules.CheckChange(manager, entry, EntryAction.Delete, null, Today, Ktm));
    }

    [Fact]
    public void Custom_permission_can_lift_a_template_restriction()
    {
        // Owner customises an Entry Person: drop both restrictions, allow editing others.
        var trusted = Member(CompanyRole.EntryPerson,
            custom: [.. RoleTemplates.For(CompanyRole.EntryPerson).Where(p => !Permission.IsRestriction(p)), Permission.EntriesEditOthers]);
        var old = Entry(Member(CompanyRole.Accountant), Today.AddDays(-5));

        Assert.Null(EntryRules.CheckCreate(trusted, TransactionType.PaymentIn, Today.AddDays(-3), Today));
        Assert.Null(EntryRules.CheckChange(trusted, old, EntryAction.Edit, Today.AddDays(-5), Today, Ktm));
    }

    [Fact]
    public void Every_transaction_type_maps_to_a_permission()
    {
        foreach (var type in Enum.GetValues<TransactionType>())
        {
            foreach (var action in Enum.GetValues<EntryAction>())
            {
                Assert.True(Permission.IsKnown(EntryRules.PermissionFor(type, action)));
            }
        }
    }

    // ---------- Staff rules ----------

    [Fact]
    public void Owner_can_invite_any_non_owner_role()
    {
        var owner = Member(CompanyRole.Owner);

        foreach (var role in Enum.GetValues<CompanyRole>().Where(r => r != CompanyRole.Owner))
        {
            Assert.Null(StaffRules.CheckGrant(owner, role, null, isNewMember: true));
        }
    }

    [Fact]
    public void Nobody_can_make_a_second_owner()
    {
        Assert.Contains("Owner", StaffRules.CheckGrant(Member(CompanyRole.Owner), CompanyRole.Owner, null, isNewMember: true));
    }

    [Fact]
    public void Cannot_grant_what_you_dont_have()
    {
        var partner = Member(CompanyRole.Partner);

        Assert.Contains(Permission.BusinessDelete,
            StaffRules.CheckGrant(partner, CompanyRole.Accountant, [Permission.CustomersView, Permission.BusinessDelete], isNewMember: true));
    }

    [Fact]
    public void Cannot_lift_a_restriction_that_applies_to_you()
    {
        // An Entry Person who was given staff.create may invite, but not without their own restrictions.
        var clerk = Member(CompanyRole.EntryPerson, custom: [.. RoleTemplates.For(CompanyRole.EntryPerson), Permission.StaffCreate]);

        Assert.Contains(Permission.RestrictLockDate,
            StaffRules.CheckGrant(clerk, CompanyRole.EntryPerson, [Permission.CustomersView], isNewMember: true));
        Assert.Null(StaffRules.CheckGrant(clerk, CompanyRole.EntryPerson, null, isNewMember: true));
    }

    [Fact]
    public void Roles_without_staff_permissions_cannot_invite()
    {
        Assert.NotNull(StaffRules.CheckGrant(Member(CompanyRole.Accountant), CompanyRole.EntryPerson, null, isNewMember: true));
        Assert.NotNull(StaffRules.CheckGrant(Member(CompanyRole.Manager), CompanyRole.EntryPerson, null, isNewMember: true));
    }

    [Fact]
    public void Unknown_permission_is_rejected()
    {
        Assert.Contains("Unknown", StaffRules.CheckGrant(Member(CompanyRole.Owner), CompanyRole.Partner, ["superpowers"], isNewMember: true));
    }

    [Fact]
    public void Owner_and_self_cannot_be_managed()
    {
        var owner = Member(CompanyRole.Owner);
        var partner = Member(CompanyRole.Partner);
        var clerk = Member(CompanyRole.EntryPerson);

        Assert.Contains("Owner", StaffRules.CheckManage(partner, owner, revoke: true));
        Assert.Contains("own access", StaffRules.CheckManage(partner, partner, revoke: false));
        Assert.Null(StaffRules.CheckManage(partner, clerk, revoke: true));
        Assert.Null(StaffRules.CheckManage(owner, partner, revoke: false));
    }

    [Fact]
    public void Cannot_manage_someone_with_more_access()
    {
        // A narrow staff manager: can view/remove staff, little else.
        var staffAdmin = Member(CompanyRole.Partner, custom: [Permission.StaffView, Permission.StaffDelete, Permission.StaffEdit, Permission.CustomersView]);
        var partner = Member(CompanyRole.Partner);
        var narrowClerk = Member(CompanyRole.EntryPerson, custom: [Permission.CustomersView]);
        var restrictedAdmin = Member(CompanyRole.Partner, custom: [Permission.StaffDelete, Permission.CustomersView, Permission.RestrictLockDate]);

        Assert.Contains("which you don't", StaffRules.CheckManage(staffAdmin, partner, revoke: true));
        Assert.Null(StaffRules.CheckManage(staffAdmin, narrowClerk, revoke: true));
        Assert.Contains("applies to you", StaffRules.CheckManage(restrictedAdmin, narrowClerk, revoke: true));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
