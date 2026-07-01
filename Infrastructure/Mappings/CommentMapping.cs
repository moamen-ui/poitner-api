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
        b.Property(x => x.Body).HasColumnName("body").IsRequired().HasMaxLength(4000);
        b.Property(x => x.IsPrivate).HasColumnName("is_private").HasDefaultValue(false);
        b.Property(x => x.AppliedAt).HasColumnName("applied_at");
        b.Property(x => x.AppliedBy).HasColumnName("applied_by");
        b.Property(x => x.AppliedByLabel).HasColumnName("applied_by_label").HasMaxLength(256);
        b.Property(x => x.EditedAt).HasColumnName("edited_at");
        b.Property(x => x.EditedBy).HasColumnName("edited_by");
        b.Property(x => x.OwnerId).HasColumnName("owner_id");
        b.HasIndex(x => x.OwnerId);
        b.OwnsOne(x => x.Element, e =>
        {
            e.ToJson("element");
            // Belt-and-suspenders bounds on the (untrusted, browser-captured) element snapshot so a
            // malicious client cannot post a multi-megabyte JSON blob. Snapshots/styles/rules can be
            // large but are still capped; the short descriptor fields get tighter bounds.
            e.Property(x => x.Selector).HasMaxLength(2000);
            e.Property(x => x.Snapshot).HasMaxLength(8000);
            e.Property(x => x.Classes).HasMaxLength(4000);
            e.Property(x => x.ComputedStyles).HasMaxLength(8000);
            e.Property(x => x.AppliedCssRules).HasMaxLength(8000);
            e.Property(x => x.SourcePath).HasMaxLength(2000);
            e.Property(x => x.ParentInfo).HasMaxLength(4000);
            e.Property(x => x.ScreenshotUrl).HasMaxLength(2000);
            e.Property(x => x.PageUrl).HasMaxLength(2000);
            e.Property(x => x.Route).HasMaxLength(2000);
            e.Property(x => x.PageTitle).HasMaxLength(2000);
            e.Property(x => x.DeviceType).HasMaxLength(32);
        });
        // Predefined-action snapshots (multi-select) stored as a JSON collection column.
        b.OwnsMany(x => x.PickedActions, a => a.ToJson("picked_actions"));
        b.HasOne(x => x.Project).WithMany(p => p.Comments).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ProjectId, x.Status });
    }
}
