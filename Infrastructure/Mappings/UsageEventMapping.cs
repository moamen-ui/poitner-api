using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class UsageEventMapping : IEntityTypeConfiguration<UsageEvent>
{
    public void Configure(EntityTypeBuilder<UsageEvent> builder)
    {
        builder.ToTable("usage_events");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.OwnerId).HasColumnName("owner_id");
        builder
            .HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(e => e.OwnerId)
            .OnDelete(DeleteBehavior.SetNull)
            .HasConstraintName("fk_usage_events_workspaces_owner_id");
        builder.Property(e => e.ProjectId).HasColumnName("project_id");
        builder
            .HasOne<Project>()
            .WithMany()
            .HasForeignKey(e => e.ProjectId)
            .OnDelete(DeleteBehavior.SetNull);
        builder.Property(e => e.UserId).HasColumnName("user_id");

        builder.Property(e => e.Type).HasColumnName("type").HasMaxLength(40).IsRequired();

        // 10 was tight enough that "web-component" (the widget's own Source value for the
        // widget_language usage event — see PlatformInsightsService) didn't fit; widened once,
        // generously, rather than re-widening every time a new caller's name is a few chars longer.
        builder.Property(e => e.Source).HasColumnName("source").HasMaxLength(32).IsRequired();

        builder
            .Property(e => e.Meta)
            .HasColumnName("meta")
            .HasColumnType("jsonb")
            .HasMaxLength(2000);

        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasIndex(e => new
        {
            e.OwnerId,
            e.ProjectId,
            e.Type,
            e.CreatedAt,
        });

        builder
            .HasIndex(e => new { e.ProjectId, e.Type })
            .IsUnique()
            .HasFilter("type IN ('first_comment', 'first_apply')")
            .HasDatabaseName("ux_usage_events_first_per_project");

        // DB-15: widget_installed is one-shot per project — its own partial unique index rather than
        // a widened filter on the one above (widening would be DropIndex+CreateIndex, an index
        // change marker under R7; a separate index is plain CreateIndex, additive R1). The column
        // order here is (type, project_id) — deliberately REVERSED from the (project_id, type) index
        // above — because EF identifies an index by its property list: a second HasIndex on
        // (ProjectId, Type) would silently RECONFIGURE that index instead of adding this one. The
        // reversed order costs nothing: every row under this filter already has type =
        // 'widget_installed', so uniqueness and the project-id lookup behave identically either way.
        builder
            .HasIndex(e => new { e.Type, e.ProjectId })
            .IsUnique()
            .HasFilter("type = 'widget_installed'")
            .HasDatabaseName("ux_usage_events_widget_installed_per_project");
    }
}
