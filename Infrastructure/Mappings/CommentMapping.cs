using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class CommentMapping : IEntityTypeConfiguration<Comment>
{
    public void Configure(EntityTypeBuilder<Comment> b)
    {
        b.ToTable("comments");

        // BaseEntity columns
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.DeletedBy).HasColumnName("deleted_by");

        // Comment-specific columns
        b.Property(x => x.ProjectId).HasColumnName("project_id");
        b.Property(x => x.Environment).HasColumnName("environment");
        b.Property(x => x.Status).HasColumnName("status");
        b.Property(x => x.AuthorId).HasColumnName("author_id");
        b.Property(x => x.Body).HasColumnName("body").IsRequired();
        b.Property(x => x.AppliedAt).HasColumnName("applied_at");
        b.Property(x => x.AppliedBy).HasColumnName("applied_by");
        b.Property(x => x.AppliedByLabel).HasColumnName("applied_by_label").HasMaxLength(256);
        b.OwnsOne(x => x.Element, e => e.ToJson("element"));
        b.HasOne(x => x.Project).WithMany(p => p.Comments).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ProjectId, x.Status });
    }
}
