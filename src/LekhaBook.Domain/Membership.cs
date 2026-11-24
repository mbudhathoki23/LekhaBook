namespace LekhaBook.Domain;

/// <summary>
/// A user's access to one company. Server-only (not synced): the server is the only writer, and
/// staff details shouldn't be copied to every device (DECISIONS D11).
/// </summary>
public sealed class CompanyMember
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid CompanyId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Name shown in the staff list.</summary>
    public required string DisplayName { get; set; }

    public CompanyRole Role { get; set; }

    /// <summary>
    /// When set, replaces the role template entirely (grants and restrictions).
    /// Null means "use the role template". Ignored for the Owner.
    /// </summary>
    public List<string>? CustomPermissions { get; set; }

    public MemberStatus Status { get; set; } = MemberStatus.Active;

    public DateTime JoinedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public bool IsActive => Status == MemberStatus.Active;

    /// <summary>What this member may do: the custom set if there is one, otherwise the role template.</summary>
    public IReadOnlySet<string> EffectivePermissions =>
        Role == CompanyRole.Owner || CustomPermissions is null
            ? RoleTemplates.For(Role)
            : new HashSet<string>(CustomPermissions, StringComparer.Ordinal);

    public bool Has(string permission) => IsActive && EffectivePermissions.Contains(permission);
}

/// <summary>
/// An invite to join a company. Only a hash of the token is stored; the token itself is shown once
/// to the inviter. Single use, expires after <see cref="Lifetime"/>.
/// </summary>
public sealed class Invitation
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid CompanyId { get; set; }

    /// <summary>Upper-case, trimmed email the invite is for.</summary>
    public required string NormalizedEmail { get; set; }

    public required string DisplayName { get; set; }

    public CompanyRole Role { get; set; }

    public List<string>? CustomPermissions { get; set; }

    /// <summary>SHA-256 of the token, hex.</summary>
    public required string TokenHash { get; set; }

    public Guid InvitedBy { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    public DateTime? AcceptedAtUtc { get; set; }

    public Guid? AcceptedBy { get; set; }

    public DateTime? CancelledAtUtc { get; set; }

    public bool IsOpen(DateTime nowUtc) =>
        AcceptedAtUtc is null && CancelledAtUtc is null && nowUtc < ExpiresAtUtc;

    public static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();
}
