using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class AppEnvironmentMapping : IEntityTypeConfiguration<AppEnvironment>
{
    public void Configure(EntityTypeBuilder<AppEnvironment> b)
    {
        b.ToTable("app_environments");

        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.DeletedBy).HasColumnName("deleted_by");

        b.Property(x => x.Name).HasColumnName("name").IsRequired().HasMaxLength(64);
        b.HasIndex(x => new { x.Name, x.OwnerId }).IsUnique();
        b.Property(x => x.OwnerId).HasColumnName("owner_id");
        b.HasIndex(x => x.OwnerId);
    }
}
