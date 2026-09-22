using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class ApiKeyMapping : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> b)
    {
        b.ToTable("api_keys");

        // BaseEntity columns
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.DeletedBy).HasColumnName("deleted_by");

        // ApiKey-specific columns
        b.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
        b.Property(x => x.OwnerId).HasColumnName("owner_id");
        b.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(x => x.OwnerId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_api_keys_workspaces_owner_id");
        b.Property(x => x.Prefix).HasColumnName("prefix").IsRequired().HasMaxLength(16);
        b.Property(x => x.Hash).HasColumnName("hash").IsRequired().HasMaxLength(64);
        b.Property(x => x.Encrypted).HasColumnName("encrypted").IsRequired();
        b.Property(x => x.Scopes).HasColumnName("scopes").IsRequired();
        b.Property(x => x.Label).HasColumnName("label").HasMaxLength(60);
        b.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
        b.Property(x => x.RevokedAt).HasColumnName("revoked_at");

        b.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // The login path matches on the hash alone, so it must be unique and indexed.
        b.HasIndex(x => x.Hash).IsUnique().HasDatabaseName("ux_api_keys_hash");

        b.HasIndex(x => x.UserId).HasDatabaseName("ix_api_keys_user_id");

        // One live key per membership (identity, workspace) — DB-11a. Enforced by the database
        // rather than only by the service: a race between two "regenerate" clicks would otherwise
        // leave two usable keys for the same (user, workspace) pair. AreNullsDistinct(false) so a
        // super admin's null-owner key is also unique.
        b.HasIndex(x => new { x.UserId, x.OwnerId })
            .IsUnique()
            .HasFilter("revoked_at IS NULL AND deleted_at IS NULL")
            .AreNullsDistinct(false)
            .HasDatabaseName("ux_api_keys_active_per_membership");
    }
}
