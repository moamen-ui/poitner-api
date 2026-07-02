using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class PlanMapping : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> b)
    {
        b.ToTable("plans");

        // BaseEntity columns
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.DeletedBy).HasColumnName("deleted_by");

        // Plan-specific columns
        b.Property(x => x.Name).HasColumnName("name").IsRequired().HasMaxLength(64);
        b.HasIndex(x => x.Name).IsUnique();
        b.Property(x => x.Slug).HasColumnName("slug").IsRequired().HasMaxLength(64);
        b.HasIndex(x => x.Slug).IsUnique();
        b.Property(x => x.PriceMonthly).HasColumnName("price_monthly").HasColumnType("numeric(12,2)");
        b.Property(x => x.Currency).HasColumnName("currency").IsRequired().HasMaxLength(8);
        b.Property(x => x.Interval).HasColumnName("interval");
        b.Property(x => x.SortOrder).HasColumnName("sort_order");
        b.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);
        b.Property(x => x.DisplayState).HasColumnName("display_state");

        // Marketing bullets stored as a JSON list column (order preserved). A value-comparer lets EF
        // track element-level mutations correctly.
        b.Property(x => x.FeatureBullets)
            .HasColumnName("feature_bullets")
            .HasColumnType("jsonb")
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new List<string>(),
                new ValueComparer<List<string>>(
                    (a, c) => (a ?? new()).SequenceEqual(c ?? new()),
                    v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
                    v => v.ToList()));

        // Entitlements: typed owned VO serialized to one JSON column (mirrors Comment.Element).
        b.OwnsOne(x => x.Entitlements, e => e.ToJson("entitlements"));
    }
}
