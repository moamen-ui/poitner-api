using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class InviteMapping : IEntityTypeConfiguration<Invite>
{
    public void Configure(EntityTypeBuilder<Invite> b)
    {
        b.ToTable("invites");

        // BaseEntity columns
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.DeletedBy).HasColumnName("deleted_by");

        // Invite-specific columns
        b.Property(x => x.OwnerId).HasColumnName("owner_id").IsRequired(); // tenant boundary — never null
        b.Property(x => x.Code).HasColumnName("code").IsRequired().HasMaxLength(64);
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.RoleId).HasColumnName("role_id");
        b.Property(x => x.Email).HasColumnName("email").HasMaxLength(256);
        b.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        b.Property(x => x.MaxUses).HasColumnName("max_uses");
        b.Property(x => x.Uses).HasColumnName("uses");
        b.Property(x => x.RevokedAt).HasColumnName("revoked_at");
        b.HasIndex(x => x.OwnerId);
    }
}
