using Microsoft.EntityFrameworkCore;

namespace EgymGarminSync.Web;

internal sealed class SyncDbContext(DbContextOptions<SyncDbContext> options) : DbContext(options)
{
    public DbSet<CachedSession> Sessions => Set<CachedSession>();
    public DbSet<SyncCheckpoint> Checkpoints => Set<SyncCheckpoint>();
    public DbSet<GarminSnapshot> GarminSnapshots => Set<GarminSnapshot>();
    public DbSet<GarminSetsCache> GarminSets => Set<GarminSetsCache>();
    public DbSet<GarminWriteJournal> WriteJournal => Set<GarminWriteJournal>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<CachedSession>().HasKey(x => new { x.Source, x.SourceId });
        builder.Entity<CachedSession>().HasIndex(x => new { x.Source, x.StartUtc });
        builder.Entity<SyncCheckpoint>().HasKey(x => x.Source);
        builder.Entity<GarminSetsCache>().HasKey(x => x.GarminActivityId);
        builder.Entity<GarminWriteJournal>().HasKey(x => x.Id);
        builder.Entity<GarminSnapshot>().HasKey(x => x.Id);
        builder.Entity<GarminSnapshot>().HasIndex(x => new { x.GarminActivityId, x.CapturedUtc });
    }
}

internal sealed class CachedSession
{
    public string Source { get; set; } = "";
    public string SourceId { get; set; } = "";
    public DateTimeOffset? StartUtc { get; set; }
    public string? Method { get; set; }
    public string? Name { get; set; }
    public int ExerciseCount { get; set; }
    public string SourceJson { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public DateTimeOffset FetchedUtc { get; set; }
}

internal sealed class SyncCheckpoint
{
    public string Source { get; set; } = "";
    public DateTimeOffset LastSuccessfulUtc { get; set; }
    public int LastImportedCount { get; set; }
    public string? Error { get; set; }
}

/// <summary>Append-only snapshot record. Never update an existing snapshot.</summary>
internal sealed class GarminSnapshot
{
    public long Id { get; set; }
    public long GarminActivityId { get; set; }
    public DateTimeOffset CapturedUtc { get; set; }
    public string Kind { get; set; } = "";
    public string ActivityJson { get; set; } = "";
    public string ExerciseSetsJson { get; set; } = "";
    public string ActivitySha256 { get; set; } = "";
    public string SetsSha256 { get; set; } = "";
}

internal sealed class GarminSetsCache
{
    public long GarminActivityId { get; set; }
    public string ExerciseSetsJson { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public DateTimeOffset FetchedUtc { get; set; }
}

internal sealed class GarminWriteJournal
{
    public long Id { get; set; }
    public long GarminActivityId { get; set; }
    public long ApprovedSnapshotId { get; set; }
    public long FreshSnapshotId { get; set; }
    public string Status { get; set; } = "";
    public string OriginalSha256 { get; set; } = "";
    public string CandidateSha256 { get; set; } = "";
    public string ProposedPayloadSha256 { get; set; } = "";
    public string RecoveryPayloadSha256 { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public string? ErrorCode { get; set; }
}
