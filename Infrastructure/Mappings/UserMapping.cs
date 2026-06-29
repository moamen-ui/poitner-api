using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class UserMapping : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users");

        // BaseEntity columns
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.DeletedBy).HasColumnName("deleted_by");

        // User-specific columns
        b.Property(x => x.PublicId).HasColumnName("public_id").IsRequired();
        b.HasIndex(x => x.PublicId).IsUnique();
        b.Property(x => x.Email).HasColumnName("email").IsRequired().HasMaxLength(256);
        b.HasIndex(x => new { x.Email, x.OwnerId }).IsUnique();
        b.Property(x => x.PasswordHash).HasColumnName("password_hash").IsRequired();
        b.Property(x => x.DisplayName).HasColumnName("display_name").IsRequired().HasMaxLength(128);
        b.Property(x => x.RoleId).HasColumnName("role_id").IsRequired();
        b.Property(x => x.IsActive).HasColumnName("is_active");
        b.Property(x => x.ApprovalStatus).HasColumnName("approval_status").HasDefaultValue(Pointer.Domain.Enums.ApprovalStatus.Approved);
        b.Property(x => x.Language).HasColumnName("language").HasMaxLength(8);
        b.Property(x => x.Theme).HasColumnName("theme").HasMaxLength(8);
        b.Property(x => x.OwnerId).HasColumnName("owner_id");
        b.HasIndex(x => x.OwnerId);

        b.HasOne(x => x.Role)
            .WithMany(r => r.Users)
            .HasForeignKey(x => x.RoleId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
