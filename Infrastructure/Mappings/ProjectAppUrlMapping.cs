using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class ProjectAppUrlMapping : IEntityTypeConfiguration<ProjectAppUrl>
{
    public void Configure(EntityTypeBuilder<ProjectAppUrl> b)
    {
        b.ToTable("project_app_urls");

        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.DeletedBy).HasColumnName("deleted_by");

        b.Property(x => x.ProjectId).HasColumnName("project_id").IsRequired();
        // Inverse named explicitly: leaving WithMany() empty made EF add a second, shadow FK "ProjectId1" (DB-04).
        b.HasOne(x => x.Project)
            .WithMany(p => p.ProjectAppUrls)
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Property(x => x.AppEnvironmentId).HasColumnName("app_environment_id").IsRequired();
        b.HasOne(x => x.AppEnvironment)
            .WithMany(e => e.ProjectAppUrls)
            .HasForeignKey(x => x.AppEnvironmentId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Property(x => x.Url).HasColumnName("url").IsRequired().HasMaxLength(2048);
        b.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        b.Property(x => x.OwnerId).HasColumnName("owner_id").IsRequired();
        b.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(x => x.OwnerId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_project_app_urls_workspaces_owner_id");
        // Partial: soft-deleted rows must not block re-adding the same environment's URL. The
        // service revives the deleted row anyway (see ProjectService.SetAppUrlAsync); this keeps the
        // database from turning any future miss into a duplicate-key 500.
        b.HasIndex(x => new { x.ProjectId, x.AppEnvironmentId })
            .IsUnique()
            .HasFilter("deleted_at IS NULL");
        b.HasIndex(x => x.OwnerId);
    }
}
