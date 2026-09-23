using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

/// <summary>
/// DB-RULES R1 (additive): new table, no existing column touched. DB-RULES R8 point 3
/// (query-filter exemption): no tenant/strict-own filter is applied here — like <c>DeviceLogin</c>,
/// this is an identity-level table (rows belong to the one env-seeded super-admin identity, not a
/// workspace), so the `owner_id`-based strict-own filter has nothing to key off; <c>MfaService</c>
/// gates every read/write on <c>Role.IsSuperAdmin</c> instead (§3.4). DB-RULES R14 (no PII beyond
/// user id): only <c>user_id</c> (structural child FK, R14) and a one-way hash are stored — never
/// the plain recovery code, never an e-mail or name.
/// </summary>
public class UserRecoveryCodeMapping : IEntityTypeConfiguration<UserRecoveryCode>
{
    public void Configure(EntityTypeBuilder<UserRecoveryCode> b)
    {
        b.ToTable("user_recovery_codes");

        // BaseEntity columns
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.DeletedBy).HasColumnName("deleted_by");

        // UserRecoveryCode-specific columns
        b.Property(x => x.UserId).HasColumnName("user_id").IsRequired();
        b.Property(x => x.CodeHash).HasColumnName("code_hash").IsRequired().HasMaxLength(64);
        b.Property(x => x.UsedAt).HasColumnName("used_at");

        b.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_user_recovery_codes_users_user_id");

        b.HasIndex(x => x.UserId).HasDatabaseName("ix_user_recovery_codes_user_id");
    }
}
