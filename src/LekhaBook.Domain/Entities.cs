namespace LekhaBook.Domain;

/// <summary>
/// Columns shared by every row that syncs between devices and the server (AGENT_PLAN § 6.1).
/// </summary>
public abstract class SyncEntity
{
    /// <summary>Client-generated UUIDv7, so devices never clash.</summary>
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid CompanyId { get; set; }

    /// <summary>
    /// Server change number for this row, increasing per company in commit order.
    /// On the server it is assigned on every write. On a device it is the last server version
    /// this row was synced at, so it doubles as the base version for conflict checks.
    /// 0 = never synced.
    /// </summary>
    public long Version { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public Guid? CreatedBy { get; set; }

    public Guid? UpdatedBy { get; set; }

    /// <summary>Device that made the last change.</summary>
    public string? UpdatedOnDevice { get; set; }

    /// <summary>Soft delete, so deletes sync like edits.</summary>
    public bool IsDeleted { get; set; }
}

/// <summary>The tenant. Users and memberships arrive in phase 4.</summary>
public sealed class Company
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public required string Name { get; set; }

    public string? Phone { get; set; }

    public string? Email { get; set; }

    public string? Address { get; set; }

    public string? Pan { get; set; }

    public CalendarMode Calendar { get; set; } = CalendarMode.BS;

    /// <summary>BS month the fiscal year starts in (Nepal: 4 = Shrawan).</summary>
    public int FiscalYearStartMonth { get; set; } = 4;

    public string Currency { get; set; } = "NPR";

    /// <summary>IANA time zone used for "today" (date locks, same-day edits).</summary>
    public string TimeZoneId { get; set; } = CompanyClock.DefaultTimeZoneId;

    public long Version { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public bool IsDeleted { get; set; }
}

public sealed class PartyCategory : SyncEntity
{
    public required string Name { get; set; }
}

/// <summary>
/// A party, money account, income/expense head or system account.
/// Balances are never stored: see <see cref="Balances"/>.
/// </summary>
public sealed class Account : SyncEntity
{
    public required string Name { get; set; }

    public AccountType Type { get; set; }

    /// <summary>Opening amount, always zero or positive; the sign comes from <see cref="OpeningSide"/>.</summary>
    public decimal OpeningBalance { get; set; }

    public BalanceSide OpeningSide { get; set; } = BalanceSide.Debit;

    public DateOnly? OpeningAsOf { get; set; }

    /// <summary>Seeded account that can't be deleted (Cash, Adjustment, …).</summary>
    public bool IsSystem { get; set; }

    public bool IsActive { get; set; } = true;

    // ---- Party fields ----
    public PartyType? PartyType { get; set; }

    public string? Phone { get; set; }

    public string? Email { get; set; }

    public string? Address { get; set; }

    public string? Pan { get; set; }

    public Guid? PartyCategoryId { get; set; }

    public Guid? PhotoAttachmentId { get; set; }

    // ---- Bank / wallet fields ----
    public string? AccountHolderName { get; set; }

    public string? AccountNumber { get; set; }

    public bool IsMoneyAccount => Type.IsMoney();
}

/// <summary>
/// One money movement, stored as a single row with a From (credit) and To (debit) account.
/// </summary>
public sealed class Transaction : SyncEntity
{
    public TransactionType Type { get; set; }

    /// <summary>Receipt / voucher number. Provisional on a device until the server confirms it.</summary>
    public required string VoucherNo { get; set; }

    public bool IsManualNo { get; set; }

    public bool IsProvisionalNo { get; set; }

    public DateOnly Date { get; set; }

    public decimal Amount { get; set; }

    /// <summary>Credited account (value leaves it).</summary>
    public Guid FromAccountId { get; set; }

    /// <summary>Debited account (value arrives in it).</summary>
    public Guid ToAccountId { get; set; }

    public string? Remarks { get; set; }

    public string? ChequeNo { get; set; }

    public DateOnly? ChequeDate { get; set; }

    public TransactionStatus Status { get; set; } = TransactionStatus.Posted;

    public bool CountsTowardBalance => Status == TransactionStatus.Posted && !IsDeleted;
}

/// <summary>Photo or PDF proof for a transaction, or a party's photo.</summary>
public sealed class Attachment : SyncEntity
{
    public Guid? TransactionId { get; set; }

    public Guid? AccountId { get; set; }

    public required string FileName { get; set; }

    public required string ContentType { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>Object-storage key once uploaded; null while only on the device.</summary>
    public string? StorageKey { get; set; }
}

/// <summary>Before/after record of every change to a transaction or account.</summary>
public sealed class AuditLog
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid CompanyId { get; set; }

    public required string EntityType { get; set; }

    public Guid EntityId { get; set; }

    /// <summary>Create, Update, Void, Delete, ConflictLost.</summary>
    public required string Action { get; set; }

    public string? BeforeJson { get; set; }

    public string? AfterJson { get; set; }

    public Guid? UserId { get; set; }

    public string? Device { get; set; }

    public DateTime AtUtc { get; set; }
}
