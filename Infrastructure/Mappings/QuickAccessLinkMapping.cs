using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class QuickAccessLinkMapping : IEntityTypeConfiguration<QuickAccessLink>
{
    public void Configure(EntityTypeBuilder<QuickAccessLink> b)
    {
        b.ToTable("quick_access_links");
        b.HasKey(x => x.Id);

        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.OwnerId).HasColumnName("owner_id");
        b.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
        b.Property(x => x.ProjectId).HasColumnName("project_id").IsRequired();
        b.Property(x => x.InviteId).HasColumnName("invite_id").IsRequired();
        b.Property(x => x.TokenHash).HasColumnName("token_hash").HasMaxLength(64).IsRequired();
        b.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();
        b.Property(x => x.MaxUses).HasColumnName("max_uses").HasDefaultValue(0);
        b.Property(x => x.Uses).HasColumnName("uses").HasDefaultValue(0);
        b.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
        b.Property(x => x.RevokedAt).HasColumnName("revoked_at");

        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.DeletedBy).HasColumnName("deleted_by");

        // Redemption is a lookup by hash on an anonymous, rate-limited endpoint — it must be an
        // index hit, and the uniqueness stops two links ever colliding on one hash.
        b.HasIndex(x => x.TokenHash).IsUnique();
    }
}
