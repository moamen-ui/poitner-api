using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class UsageDailyMapping : IEntityTypeConfiguration<UsageDaily>
{
    public void Configure(EntityTypeBuilder<UsageDaily> builder)
    {
        builder.ToTable("usage_daily");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.Day).HasColumnName("day").HasColumnType("date").IsRequired();

        // Operator/analytics table (DB-RULES R8.8, like usage_events): owner_id carries the filter
        // shape but the row is NOT deleted with the workspace — FK SET NULL keeps it as an
        // analytics record. Excluded from HardDeleteOrder by name (Tests/WorkspaceTests.cs).
        builder.Property(e => e.OwnerId).HasColumnName("owner_id");
        builder
            .HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(e => e.OwnerId)
            .OnDelete(DeleteBehavior.SetNull)
            .HasConstraintName("fk_usage_daily_workspaces_owner_id");

        builder.Property(e => e.Type).HasColumnName("type").HasMaxLength(40).IsRequired();
        builder.Property(e => e.Count).HasColumnName("count").IsRequired();
        builder.Property(e => e.ComputedAt).HasColumnName("computed_at").IsRequired();

        // NULLS NOT DISTINCT on Postgres so two owner-less rows for the same (day, type) can never
        // exist. Safe on Sqlite (filter-ignoring, AreNullsDistinct untranslated — see SCHEMA.md
        // "Cross-table facts") because the rollup job upserts in memory and never writes two.
        builder
            .HasIndex(e => new
            {
                e.Day,
                e.OwnerId,
                e.Type,
            })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasDatabaseName("ux_usage_daily_day_owner_type");

        builder.HasIndex(e => new { e.OwnerId, e.Day }).HasDatabaseName("ix_usage_daily_owner_day");
    }
}
