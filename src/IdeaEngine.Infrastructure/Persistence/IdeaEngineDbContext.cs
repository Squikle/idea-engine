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

    public DbSet<AppStateEntity> AppState => Set<AppStateEntity>();

    public DbSet<ResearchReportEntity> ResearchReports => Set<ResearchReportEntity>();

    public DbSet<JobEntity> Jobs => Set<JobEntity>();

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
            signal.Property(x => x.Glance).HasMaxLength(200);
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
            idea.Property(x => x.Origin).HasMaxLength(16);
            idea.HasIndex(x => x.Verified);
            idea.Property(x => x.NotesJson).HasColumnType("jsonb");
            idea.Property(x => x.AppealJson).HasColumnType("jsonb");
            idea.Property(x => x.RelatedJson).HasColumnType("jsonb");
            idea.Property(x => x.Playbook).HasMaxLength(64);
            idea.Property(x => x.VariantsJson).HasColumnType("jsonb");
            idea.Property(x => x.EvidenceJson).HasColumnType("jsonb");
            idea.Property(x => x.ScoresJson).HasColumnType("jsonb");
            idea.Property(x => x.SkepticJson).HasColumnType("jsonb");
            idea.Property(x => x.BuilderModel).HasMaxLength(128);
            idea.Property(x => x.SkepticModel).HasMaxLength(128);
            idea.Property(x => x.CostUsd).HasPrecision(10, 6);
        });

        modelBuilder.Entity<AppStateEntity>(state =>
        {
            state.ToTable("app_state");
            state.HasKey(x => x.Key);
            state.Property(x => x.Key).HasMaxLength(64);
        });

        modelBuilder.Entity<JobEntity>(job =>
        {
            job.ToTable("jobs");
            job.HasIndex(x => x.Status);
            job.Property(x => x.Kind).HasMaxLength(24);
            job.Property(x => x.Status).HasMaxLength(16);
            job.Property(x => x.PayloadJson).HasColumnType("jsonb");
            job.Property(x => x.LastError).HasMaxLength(1000);
        });

        modelBuilder.Entity<ResearchReportEntity>(report =>
        {
            report.ToTable("research_reports");
            report.HasIndex(x => x.IdeaId);
            report.HasIndex(x => x.CreatedAt);
            report.Property(x => x.Verdict).HasMaxLength(16);
            report.Property(x => x.ReportJson).HasColumnType("jsonb");
            report.Property(x => x.QueriesJson).HasColumnType("jsonb");
            report.Property(x => x.Model).HasMaxLength(128);
            report.Property(x => x.EngineVersion).HasMaxLength(16);
            report.Property(x => x.CostUsd).HasPrecision(10, 6);
            report.HasOne(x => x.Idea)
                .WithMany()
                .HasForeignKey(x => x.IdeaId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
