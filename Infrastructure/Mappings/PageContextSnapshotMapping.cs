using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class PageContextSnapshotMapping : IEntityTypeConfiguration<PageContextSnapshot>
{
    public void Configure(EntityTypeBuilder<PageContextSnapshot> b)
    {
        b.ToTable("page_context_snapshots");

        // BaseEntity columns
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.DeletedBy).HasColumnName("deleted_by");

        // PageContextSnapshot-specific columns
        b.Property(x => x.ProjectId).HasColumnName("project_id");
        b.Property(x => x.Environment).HasColumnName("environment");
        b.Property(x => x.Route).HasColumnName("route").IsRequired().HasMaxLength(512);
        b.Property(x => x.SessionId).HasColumnName("session_id").IsRequired().HasMaxLength(64);
        b.Property(x => x.OwnerId).HasColumnName("owner_id");
        b.Property(x => x.LastEventAt).HasColumnName("last_event_at");

        // Untrusted, browser-captured data — same belt-and-suspenders bounds as Comment.Element.
        b.OwnsMany(x => x.ConsoleEntries, e =>
        {
            e.ToJson("console_entries");
            e.Property(p => p.Level).HasMaxLength(16);
            e.Property(p => p.Message).HasMaxLength(2000);
            e.Property(p => p.Stack).HasMaxLength(4000);
        });
        b.OwnsMany(x => x.NetworkEntries, e =>
        {
            e.ToJson("network_entries");
            e.Property(p => p.Method).HasMaxLength(16);
            e.Property(p => p.Url).HasMaxLength(2000);
        });

        b.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.OwnerId);
        // Dedup lookup: CommentService.CreateAsync looks up an existing snapshot for the same
        // page/visit by this composite key before creating a new one.
        b.HasIndex(x => new { x.ProjectId, x.Route, x.Environment, x.SessionId });
    }
}
