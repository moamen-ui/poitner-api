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
        builder.Property(e => e.ProjectId).HasColumnName("project_id");
        builder.Property(e => e.UserId).HasColumnName("user_id");
        
        builder.Property(e => e.Type)
            .HasColumnName("type")
            .HasMaxLength(40)
            .IsRequired();
            
        // 10 was tight enough that "web-component" (the widget's own Source value for the
        // widget_language usage event — see PlatformInsightsService) didn't fit; widened once,
        // generously, rather than re-widening every time a new caller's name is a few chars longer.
        builder.Property(e => e.Source)
            .HasColumnName("source")
            .HasMaxLength(32)
            .IsRequired();
            
        builder.Property(e => e.Meta)
            .HasColumnName("meta")
            .HasColumnType("jsonb")
            .HasMaxLength(2000);
            
        builder.Property(e => e.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.HasIndex(e => new { e.OwnerId, e.ProjectId, e.Type, e.CreatedAt });
        
        builder.HasIndex(e => new { e.ProjectId, e.Type })
            .IsUnique()
            .HasFilter("type IN ('first_comment', 'first_apply')")
            .HasDatabaseName("ux_usage_events_first_per_project");
    }
}
