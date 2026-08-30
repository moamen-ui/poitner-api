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
        b.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);

        b.Property(x => x.AppEnvironmentId).HasColumnName("app_environment_id").IsRequired();
        b.HasOne(x => x.AppEnvironment).WithMany(e => e.ProjectAppUrls).HasForeignKey(x => x.AppEnvironmentId).OnDelete(DeleteBehavior.Cascade);

        b.Property(x => x.Url).HasColumnName("url").IsRequired().HasMaxLength(2048);
        b.Property(x => x.OwnerId).HasColumnName("owner_id").IsRequired();
        b.HasIndex(x => new { x.ProjectId, x.AppEnvironmentId }).IsUnique();
        b.HasIndex(x => x.OwnerId);
    }
}
