using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class ExtensionSiteMapping : IEntityTypeConfiguration<ExtensionSite>
{
    public void Configure(EntityTypeBuilder<ExtensionSite> b)
    {
        b.ToTable("extension_sites");

        // BaseEntity columns
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.DeletedBy).HasColumnName("deleted_by");

        // ExtensionSite-specific columns
        b.Property(x => x.OwnerId).HasColumnName("owner_id").IsRequired(); // tenant boundary — never null
        b.Property(x => x.Origin).HasColumnName("origin").IsRequired().HasMaxLength(256);
        b.Property(x => x.FirstSeenAt).HasColumnName("first_seen_at");
        b.HasIndex(x => new { x.OwnerId, x.Origin }).IsUnique();
    }
}
