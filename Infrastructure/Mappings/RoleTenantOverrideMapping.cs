using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class RoleTenantOverrideMapping : IEntityTypeConfiguration<RoleTenantOverride>
{
    public void Configure(EntityTypeBuilder<RoleTenantOverride> b)
    {
        b.ToTable("role_tenant_overrides");

        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.DeletedBy).HasColumnName("deleted_by");

        b.Property(x => x.RoleId).HasColumnName("role_id").IsRequired();
        b.Property(x => x.OwnerId).HasColumnName("owner_id").IsRequired();
        b.Property(x => x.IsActive).HasColumnName("is_active");
        b.HasIndex(x => new { x.RoleId, x.OwnerId }).IsUnique();
    }
}
