using IdeaEngine.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace IdeaEngine.Infrastructure.Persistence;

public sealed class IdeaEngineDbContext(DbContextOptions<IdeaEngineDbContext> options)
    : DbContext(options)
{
    public DbSet<RawItemEntity> RawItems => Set<RawItemEntity>();

    public DbSet<PipelineRunEntity> PipelineRuns => Set<PipelineRunEntity>();

    public DbSet<AiLedgerEntry> AiLedger => Set<AiLedgerEntry>();

    public DbSet<SignalEntity> Signals => Set<SignalEntity>();

    public DbSet<IdeaEntity> Ideas => Set<IdeaEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<RawItemEntity>(item =>
        {
            item.ToTable("raw_items");
            item.HasIndex(x => new { x.Source, x.ExternalId }).IsUnique();
            item.HasIndex(x => x.Status);
            item.HasIndex(x => x.ContentHash);
            item.HasIndex(x => x.FetchedAt);

            item.Property(x => x.ExternalId).HasMaxLength(128);
            item.Property(x => x.Title).HasMaxLength(2048);
            item.Property(x => x.Url).HasMaxLength(2048);
            item.Property(x => x.Author).HasMaxLength(256);
            item.Property(x => x.Community).HasMaxLength(128);
            item.Property(x => x.ContentHash).HasMaxLength(64);
            item.Property(x => x.CommentsJson).HasColumnType("jsonb");
            item.Property(x => x.RawPayloadJson).HasColumnType("jsonb");
            item.Property(x => x.Embedding).HasColumnType("vector(384)");
        });

        modelBuilder.Entity<PipelineRunEntity>(run =>
        {
            run.ToTable("pipeline_runs");
            run.HasIndex(x => x.StartedAt);
            run.Property(x => x.Stage).HasMaxLength(64);
            run.Property(x => x.Notes).HasMaxLength(2048);
            run.Property(x => x.CostUsd).HasPrecision(10, 6);
        });

        modelBuilder.Entity<AiLedgerEntry>(entry =>
        {
            entry.ToTable("ai_ledger");
            entry.HasIndex(x => new { x.Day, x.Stage });
            entry.Property(x => x.Stage).HasMaxLength(64);
            entry.Property(x => x.Model).HasMaxLength(128);
            entry.Property(x => x.CostUsd).HasPrecision(10, 6);
        });

        modelBuilder.Entity<SignalEntity>(signal =>
        {
            signal.ToTable("signals");
            signal.HasIndex(x => x.RawItemId);
            signal.HasIndex(x => x.CreatedAt);
            signal.Property(x => x.Kind).HasMaxLength(32);
            signal.Property(x => x.Summary).HasMaxLength(1000);
            signal.Property(x => x.Audience).HasMaxLength(300);
            signal.Property(x => x.CommercialSentiment).HasMaxLength(48);
            signal.Property(x => x.Model).HasMaxLength(128);
            signal.HasOne(x => x.RawItem)
                .WithMany()
                .HasForeignKey(x => x.RawItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IdeaEntity>(idea =>
        {
            idea.ToTable("ideas");
            idea.HasIndex(x => x.CreatedAt);
            idea.HasIndex(x => x.Status);
            idea.Property(x => x.Title).HasMaxLength(300);
            idea.Property(x => x.Category).HasMaxLength(32);
            idea.Property(x => x.TargetUser).HasMaxLength(300);
            idea.Property(x => x.Monetization).HasMaxLength(600);
            idea.Property(x => x.DistributionNote).HasMaxLength(400);
            idea.Property(x => x.Status).HasMaxLength(24);
            idea.Property(x => x.EvidenceJson).HasColumnType("jsonb");
            idea.Property(x => x.ScoresJson).HasColumnType("jsonb");
            idea.Property(x => x.SkepticJson).HasColumnType("jsonb");
            idea.Property(x => x.BuilderModel).HasMaxLength(128);
            idea.Property(x => x.SkepticModel).HasMaxLength(128);
            idea.Property(x => x.CostUsd).HasPrecision(10, 6);
        });
    }
}
