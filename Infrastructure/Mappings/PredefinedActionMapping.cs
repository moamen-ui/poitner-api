using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class PredefinedActionMapping : IEntityTypeConfiguration<PredefinedAction>
{
    public void Configure(EntityTypeBuilder<PredefinedAction> b)
    {
        b.ToTable("predefined_actions");

        // BaseEntity columns
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.DeletedBy).HasColumnName("deleted_by");

        // PredefinedAction-specific columns
        b.Property(x => x.OwnerId).HasColumnName("owner_id").IsRequired();
        b.Property(x => x.ProjectId).HasColumnName("project_id");
        b.Property(x => x.UserId).HasColumnName("user_id");
        // Text bounded ≤256; Prompt is Postgres `text` (multi-paragraph — no length cap).
        b.Property(x => x.Text).HasColumnName("text").IsRequired().HasMaxLength(256);
        b.Property(x => x.Prompt).HasColumnName("prompt").HasColumnType("text").IsRequired();
        b.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        b.Property(x => x.SortOrder).HasColumnName("sort_order");

        // btree indexes over NULLable columns still serve tenant-wide (ProjectId IS NULL) rows.
        b.HasIndex(x => x.OwnerId);
        b.HasIndex(x => new { x.OwnerId, x.ProjectId });
    }
}
