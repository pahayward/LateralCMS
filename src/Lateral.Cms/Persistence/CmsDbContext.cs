using System.Text.Json;
using Lateral.Cms.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Lateral.Cms.Persistence;

/// <summary>
/// The shared model. Two concrete contexts derive from it so that reads and writes can be configured (and, in a
/// larger deployment, pointed at different databases) independently.
/// </summary>
public abstract class CmsDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<EntityRow> Entities => Set<EntityRow>();

    public DbSet<TombstoneRow> Tombstones => Set<TombstoneRow>();

    public DbSet<BatchRow> Batches => Set<BatchRow>();

    public DbSet<EventRow> Events => Set<EventRow>();

    public DbSet<AssetRow> Assets => Set<AssetRow>();

    public DbSet<UploadRow> Uploads => Set<UploadRow>();

    public DbSet<UploadPartRow> UploadParts => Set<UploadPartRow>();

    public DbSet<AuditRow> AuditLog => Set<AuditRow>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite has no timezone-aware type and hands back DateTimeKind.Unspecified. Everything stored is UTC, so
        // say so on the way out; the way in only normalises stray local values.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();

        // Readable lower-case words in the file, so the CHECK constraint and raw-SQL queries say what they mean.
        configurationBuilder.Properties<EventOutcome>().HaveConversion<SnakeCaseEnumConverter<EventOutcome>>().HaveMaxLength(32);
        configurationBuilder.Properties<BatchState>().HaveConversion<SnakeCaseEnumConverter<BatchState>>().HaveMaxLength(16);
        configurationBuilder.Properties<CmsEventType>().HaveConversion<SnakeCaseEnumConverter<CmsEventType>>().HaveMaxLength(16);
        configurationBuilder.Properties<AssetState>().HaveConversion<SnakeCaseEnumConverter<AssetState>>().HaveMaxLength(16);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EntityRow>(entity =>
        {
            entity.ToTable("Entities");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasMaxLength(200);
            entity.Property(e => e.Payload).IsRequired();
            entity.Property(e => e.PayloadChecksum).HasMaxLength(100).IsRequired();
            entity.Property(e => e.DisabledBy).HasMaxLength(320);
            entity.Property(e => e.ConcurrencyStamp).IsConcurrencyToken();

            // Serves the user-facing listing. EF turns `IsPublished && !IsDisabledByAdmin` into a bare boolean test,
            // which SQLite cannot search a composite index on; a partial index whose condition is that same test is
            // used instead, holds only visible rows, and is already in Id order. (ListingIndexTests checks the plan
            // of the SQL EF really generates.)
            entity.HasIndex(e => e.Id)
                .HasDatabaseName("IX_Entities_Visible")
                .HasFilter("\"IsPublished\" AND NOT \"IsDisabledByAdmin\"");
        });

        modelBuilder.Entity<TombstoneRow>(tombstone =>
        {
            tombstone.ToTable("Tombstones");
            tombstone.HasKey(t => t.Id);
            tombstone.Property(t => t.Id).HasMaxLength(200);
        });

        modelBuilder.Entity<BatchRow>(batch =>
        {
            batch.ToTable("Batches");
            batch.HasKey(b => b.Id);
            batch.HasAlternateKey(b => b.BatchId);
            batch.Property(b => b.LastError).HasMaxLength(200);

            // Serves "which batch next": queued only, takedowns first, then oldest. The index is in that order, so
            // picking one is a short walk however long the backlog, and the queue-size sums are index seeks.
            batch.HasIndex(b => new { b.Status, b.HasTakedown, b.Id })
                .HasDatabaseName("IX_Batches_Pick")
                .IsDescending(false, true, false);

            batch.HasMany(b => b.Events)
                .WithOne()
                .HasPrincipalKey(b => b.BatchId)
                .HasForeignKey(e => e.BatchId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EventRow>(row =>
        {
            row.ToTable("Events", table =>
                // A decided event can never hold a payload: once the outcome is known the confidential text is gone.
                table.HasCheckConstraint("CK_Events_PayloadOnlyWhileQueued", "\"Outcome\" = 'queued' OR \"Payload\" IS NULL"));
            row.HasKey(e => e.Id);
            row.Property(e => e.EntityId).HasMaxLength(200);
            row.Property(e => e.RawType).HasMaxLength(64).IsRequired();
            row.Property(e => e.Reason).HasMaxLength(64);
            row.Property(e => e.PayloadChecksum).HasMaxLength(100);
            row.HasIndex(e => new { e.BatchId, e.Sequence }).IsUnique();
            row.HasIndex(e => e.EntityId);
        });

        modelBuilder.Entity<AssetRow>(asset =>
        {
            asset.ToTable("Assets");
            asset.HasKey(a => a.Id);
            asset.Property(a => a.Key).HasMaxLength(200).IsRequired();
            asset.Property(a => a.ContentType).HasMaxLength(100).IsRequired();
            asset.Property(a => a.DetectedType).HasMaxLength(100);
            asset.Property(a => a.Sha256).HasMaxLength(64).IsRequired();
            asset.Property(a => a.ScannerName).HasMaxLength(64);
            asset.Property(a => a.ScannerVersion).HasMaxLength(64);
            asset.Property(a => a.Verdict).HasMaxLength(32);
            asset.Property(a => a.Signature).HasMaxLength(100);
            asset.Property(a => a.LastError).HasMaxLength(200);

            // Serves "which asset to scan next" (quarantined, due, oldest first) and the quota sums, as index seeks.
            asset.HasIndex(a => new { a.State, a.NextAttemptAt, a.CreatedAt }).HasDatabaseName("IX_Assets_Scan");
            asset.HasIndex(a => a.Sha256);
        });

        modelBuilder.Entity<UploadRow>(upload =>
        {
            upload.ToTable("Uploads");
            upload.HasKey(u => u.UploadId);
            upload.Property(u => u.Key).HasMaxLength(200).IsRequired();
            upload.Property(u => u.ContentType).HasMaxLength(100).IsRequired();
            upload.HasIndex(u => u.ExpiresAt);
        });

        modelBuilder.Entity<UploadPartRow>(part =>
        {
            part.ToTable("UploadParts");
            part.HasKey(p => new { p.UploadId, p.PartNumber });
            part.Property(p => p.Sha256).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<AuditRow>(audit =>
        {
            audit.ToTable("AuditLog");
            audit.HasKey(a => a.Id);
            audit.Property(a => a.Actor).HasMaxLength(320).IsRequired();
            audit.Property(a => a.Action).HasMaxLength(64).IsRequired();
            audit.Property(a => a.Target).HasMaxLength(200).IsRequired();
            audit.Property(a => a.Detail).HasMaxLength(500);
        });
    }
}

public sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    value => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value,
    value => DateTime.SpecifyKind(value, DateTimeKind.Utc));

/// <summary>Stores an enum as its snake_case name (<c>IgnoredStale</c> becomes <c>ignored_stale</c>).</summary>
public sealed class SnakeCaseEnumConverter<T>() : ValueConverter<T, string>(
    value => SnakeCaseNames<T>.ToText(value),
    text => SnakeCaseNames<T>.FromText(text))
    where T : struct, Enum;

/// <summary>Name lookups for <see cref="SnakeCaseEnumConverter{T}"/>, built once per enum.</summary>
public static class SnakeCaseNames<T>
    where T : struct, Enum
{
    private static readonly Dictionary<T, string> ToName =
        Enum.GetValues<T>().ToDictionary(v => v, v => JsonNamingPolicy.SnakeCaseLower.ConvertName(v.ToString()));

    private static readonly Dictionary<string, T> FromName = ToName.ToDictionary(p => p.Value, p => p.Key);

    public static string ToText(T value) => ToName[value];

    public static T FromText(string text) => FromName[text];
}
