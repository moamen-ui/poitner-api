using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class StatusPresentationMapping : IEntityTypeConfiguration<StatusPresentation>
{
    public void Configure(EntityTypeBuilder<StatusPresentation> b)
    {
        b.ToTable("status_presentations");
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.DeletedBy).HasColumnName("deleted_by");
        b.Property(x => x.StatusValue).HasColumnName("status_value").IsRequired();
        b.HasIndex(x => new { x.StatusValue, x.OwnerId }).IsUnique();
        b.Property(x => x.Label).HasColumnName("label").HasMaxLength(64);
        b.Property(x => x.Color).HasColumnName("color").HasMaxLength(9);
        b.Property(x => x.DisplayOrder).HasColumnName("display_order");
        b.Property(x => x.OwnerId).HasColumnName("owner_id");
        b.HasIndex(x => x.OwnerId);
    }
}
