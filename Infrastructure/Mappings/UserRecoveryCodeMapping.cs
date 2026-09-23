using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

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
