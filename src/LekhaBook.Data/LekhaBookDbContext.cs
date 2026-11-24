using LekhaBook.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LekhaBook.Data;

/// <summary>
/// The model shared by the device database (SQLite) and the server database (PostgreSQL).
/// Each provider's context derives from this and adds only its own tables.
/// </summary>
public abstract class LekhaBookDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<Company> Companies => Set<Company>();

    public DbSet<Account> Accounts => Set<Account>();

    public DbSet<PartyCategory> PartyCategories => Set<PartyCategory>();

    public DbSet<Transaction> Transactions => Set<Transaction>();

    public DbSet<Attachment> Attachments => Set<Attachment>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Every DateTime is UTC: Npgsql needs Kind=Utc for timestamptz, and reads come back as Utc.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();

        // Money: numeric(18,2) on PostgreSQL. SQLite stores decimals as text and EF Core
        // translates Sum/compare/order with its ef_* functions.
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);

        // Enums as readable text, so reordering an enum can't silently change stored data.
        StoreAsString<AccountType>(configurationBuilder);
        StoreAsString<PartyType>(configurationBuilder);
        StoreAsString<BalanceSide>(configurationBuilder);
        StoreAsString<TransactionType>(configurationBuilder);
        StoreAsString<TransactionStatus>(configurationBuilder);
        StoreAsString<CalendarMode>(configurationBuilder);
        StoreAsString<CompanyRole>(configurationBuilder);
        StoreAsString<MemberStatus>(configurationBuilder);
    }

    private static void StoreAsString<TEnum>(ModelConfigurationBuilder builder)
        where TEnum : struct, Enum
    {
        builder.Properties<TEnum>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<TEnum?>().HaveConversion<string>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Company>(e =>
        {
            e.ToTable("Companies");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Phone).HasMaxLength(32);
            e.Property(x => x.Email).HasMaxLength(200);
            e.Property(x => x.Address).HasMaxLength(300);
            e.Property(x => x.Pan).HasMaxLength(20);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.TimeZoneId).HasMaxLength(64);
        });

        modelBuilder.Entity<PartyCategory>(e =>
        {
            e.ToTable("PartyCategories");
            ConfigureSyncEntity(e);
            e.Property(x => x.Name).HasMaxLength(100);
        });

        modelBuilder.Entity<Account>(e =>
        {
            e.ToTable("Accounts");
            ConfigureSyncEntity(e);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Phone).HasMaxLength(32);
            e.Property(x => x.Email).HasMaxLength(200);
            e.Property(x => x.Address).HasMaxLength(300);
            e.Property(x => x.Pan).HasMaxLength(20);
            e.Property(x => x.AccountHolderName).HasMaxLength(200);
            e.Property(x => x.AccountNumber).HasMaxLength(50);
            e.HasOne<PartyCategory>().WithMany().HasForeignKey(x => x.PartyCategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.CompanyId, x.Type, x.Name });
        });

        modelBuilder.Entity<Transaction>(e =>
        {
            e.ToTable("Transactions");
            ConfigureSyncEntity(e);
            e.Property(x => x.VoucherNo).HasMaxLength(40);
            e.Property(x => x.Remarks).HasMaxLength(500);
            e.Property(x => x.ChequeNo).HasMaxLength(40);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.FromAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.ToAccountId).OnDelete(DeleteBehavior.Restrict);

            // Ledger and balance queries filter by account and date range.
            e.HasIndex(x => new { x.CompanyId, x.FromAccountId, x.Date });
            e.HasIndex(x => new { x.CompanyId, x.ToAccountId, x.Date });
            e.HasIndex(x => new { x.CompanyId, x.Date });
            e.HasIndex(x => new { x.CompanyId, x.Type, x.VoucherNo });
        });

        modelBuilder.Entity<Attachment>(e =>
        {
            e.ToTable("Attachments");
            ConfigureSyncEntity(e);
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.ContentType).HasMaxLength(100);
            e.Property(x => x.StorageKey).HasMaxLength(500);
            e.HasOne<Transaction>().WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AuditLog>(e =>
        {
            e.ToTable("AuditLogs");
            e.Property(x => x.EntityType).HasMaxLength(64);
            e.Property(x => x.Action).HasMaxLength(32);
            e.Property(x => x.Device).HasMaxLength(64);
            e.HasIndex(x => new { x.CompanyId, x.EntityType, x.EntityId });
        });
    }

    /// <summary>Columns and indexes every synced table has (AGENT_PLAN § 6.1).</summary>
    protected static void ConfigureSyncEntity<T>(EntityTypeBuilder<T> e)
        where T : SyncEntity
    {
        e.HasKey(x => x.Id);
        e.Property(x => x.UpdatedOnDevice).HasMaxLength(64);
        e.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.Restrict);

        // Pull = "rows of this company changed after version N".
        e.HasIndex(x => new { x.CompanyId, x.Version });
    }

    /// <summary>Stores DateTime as UTC and reads it back with Kind=Utc.</summary>
    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}
