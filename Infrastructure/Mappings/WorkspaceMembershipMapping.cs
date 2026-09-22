using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class WorkspaceMembershipMapping : IEntityTypeConfiguration<WorkspaceMembership>
{
    public void Configure(EntityTypeBuilder<WorkspaceMembership> b)
    {
        b.ToTable("workspace_memberships");

        // BaseEntity columns
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.DeletedBy).HasColumnName("deleted_by");

        // WorkspaceMembership-specific columns
        b.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
        b.HasOne(x => x.User)
            .WithMany(u => u.Memberships)
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Property(x => x.OwnerId).HasColumnName("owner_id").IsRequired();
        b.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(x => x.OwnerId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_workspace_memberships_workspaces_owner_id");

        b.Property(x => x.RoleId).HasColumnName("role_id").IsRequired();
        b.HasOne(x => x.Role)
            .WithMany()
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Property(x => x.IsActive).HasColumnName("is_active").IsRequired();
        b.Property(x => x.ApprovalStatus)
            .HasColumnName("approval_status")
            .HasDefaultValue(Pointer.Domain.Enums.ApprovalStatus.Approved);
        b.Property(x => x.SecurityStamp).HasColumnName("security_stamp").IsRequired();
        b.Property(x => x.JoinedAt).HasColumnName("joined_at").IsRequired();
        b.Property(x => x.LeftAt).HasColumnName("left_at");
        b.Property(x => x.LeftReason).HasColumnName("left_reason");

        b.Property(x => x.InviteId).HasColumnName("invite_id");
        b.HasOne<Invite>()
            .WithMany()
            .HasForeignKey(x => x.InviteId)
            .OnDelete(DeleteBehavior.SetNull);

        // Re-joining after removal inserts a new row (R9) — only one LIVE membership per
        // (identity, workspace) at a time.
        b.HasIndex(x => new { x.UserId, x.OwnerId })
            .IsUnique()
            .HasFilter("left_at IS NULL AND deleted_at IS NULL")
            .HasDatabaseName("ux_workspace_memberships_user_workspace_live");

        // Admin lookups within a workspace, owner-leading.
        b.HasIndex(x => new { x.OwnerId, x.RoleId })
            .HasDatabaseName("ix_workspace_memberships_owner_role");
    }
}
