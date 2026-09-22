using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class DeviceLoginMapping : IEntityTypeConfiguration<DeviceLogin>
{
    public void Configure(EntityTypeBuilder<DeviceLogin> b)
    {
        b.ToTable("device_logins");

        // BaseEntity columns
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.DeletedBy).HasColumnName("deleted_by");

        // DeviceLogin-specific columns
        b.Property(x => x.DeviceCodeHash)
            .HasColumnName("device_code_hash")
            .HasMaxLength(64)
            .IsRequired();
        b.Property(x => x.UserCode).HasColumnName("user_code").HasMaxLength(16).IsRequired();
        b.Property(x => x.ClientName).HasColumnName("client_name").HasMaxLength(200).IsRequired();
        b.Property(x => x.Status).HasColumnName("status").IsRequired();
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.OwnerId).HasColumnName("owner_id");
        b.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(x => x.OwnerId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_device_logins_workspaces_owner_id");
        b.Property(x => x.ExpiresAt).HasColumnName("expires_at").IsRequired();
        b.Property(x => x.ApprovedAt).HasColumnName("approved_at");
        b.Property(x => x.ConsumedAt).HasColumnName("consumed_at");

        // The poll path looks a row up by hash on an anonymous, rate-limited endpoint — must be an
        // index hit, and uniqueness stops two rows ever colliding on one hash.
        b.HasIndex(x => x.DeviceCodeHash)
            .IsUnique()
            .HasDatabaseName("ux_device_logins_device_code_hash");

        // Not unique: the service enforces uniqueness only among non-terminal rows (see UserCode
        // remarks on the entity), so the database allows a code to repeat once its row is terminal.
        b.HasIndex(x => x.UserCode).HasDatabaseName("ix_device_logins_user_code");
    }
}
