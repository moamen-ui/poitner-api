using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class RoleMapping : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> b)
    {
        b.ToTable("roles");

        // BaseEntity columns
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.DeletedBy).HasColumnName("deleted_by");

        // Role-specific columns
        b.Property(x => x.Name).HasColumnName("name").IsRequired().HasMaxLength(64);
        b.HasIndex(x => x.Name).IsUnique();
        b.Property(x => x.GrantsAdmin).HasColumnName("grants_admin");
        b.Property(x => x.IsSystem).HasColumnName("is_system");
        b.Property(x => x.IsActive).HasColumnName("is_active");
    }
}
