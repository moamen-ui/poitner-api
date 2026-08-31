using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class ProjectMapping : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> b)
    {
        b.ToTable("projects");

        // BaseEntity columns
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.DeletedBy).HasColumnName("deleted_by");

        // Project-specific columns
        b.Property(x => x.Key).HasColumnName("key").IsRequired().HasMaxLength(64);
        // Filtered so a deleted project's key becomes reusable — without this, recreating a
        // project with a previously-deleted key throws an unhandled unique-constraint violation
        // (a raw 500) instead of succeeding, since the soft-deleted row still occupies the slot.
        b.HasIndex(x => new { x.Key, x.OwnerId }).IsUnique().HasFilter("deleted_at IS NULL");
        b.Property(x => x.Name).HasColumnName("name").IsRequired().HasMaxLength(128);
        b.Property(x => x.IsActive).HasColumnName("is_active");
        b.Property(x => x.PageContextCaptureEnabled).HasColumnName("page_context_capture_enabled").HasDefaultValue(false);
        b.Property(x => x.AppUrl).HasColumnName("app_url").HasMaxLength(2048);
        // NOT NULL at the DB level: ProjectService.CreateAsync forbids a null-owner project (super
        // admins can no longer create/own one at all) — enforced here too so a future bug can't
        // silently reintroduce the recurring "owner_id" bug class by producing one anyway.
        b.Property(x => x.OwnerId).HasColumnName("owner_id").IsRequired();
        b.HasIndex(x => x.OwnerId);
    }
}
