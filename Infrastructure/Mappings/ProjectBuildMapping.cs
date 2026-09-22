using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class ProjectBuildMapping : IEntityTypeConfiguration<ProjectBuild>
{
    public void Configure(EntityTypeBuilder<ProjectBuild> b)
    {
        b.ToTable("project_builds");

        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.DeletedBy).HasColumnName("deleted_by");

        b.Property(x => x.ProjectId).HasColumnName("project_id");
        b.Property(x => x.Sha).HasColumnName("sha").HasMaxLength(40).IsRequired();
        b.Property(x => x.FirstSeenAt).HasColumnName("first_seen_at");
        b.Property(x => x.Source).HasColumnName("source");
        b.Property(x => x.OwnerId).HasColumnName("owner_id");
        b.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(x => x.OwnerId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_project_builds_workspaces_owner_id");

        b.HasOne(x => x.Project)
            .WithMany()
            .HasForeignKey(x => x.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // One row per (project, sha). The upsert depends on this: without it a concurrent pair of
        // reports for the same deploy would both insert, and "first seen" would stop being true.
        // Filtered on not-deleted so a soft-deleted row cannot block re-recording the same sha.
        b.HasIndex(x => new { x.ProjectId, x.Sha }).IsUnique().HasFilter("deleted_at IS NULL");
    }
}
