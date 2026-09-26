using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

/// <summary>DB-20 §3.4. Global catalog like <see cref="Plan"/> — no <c>owner_id</c>, no query filter,
/// super-admin endpoints only. ck_discount_codes_code_format/ck_discount_codes_value (Postgres regex
/// `~`) are registered Npgsql-only in <see cref="Pointer.Infrastructure.AppDbContext"/> — see the
/// comment there.</summary>
public class DiscountCodeMapping : IEntityTypeConfiguration<DiscountCode>
{
    public void Configure(EntityTypeBuilder<DiscountCode> b)
    {
        b.ToTable(
            "discount_codes",
            t =>
            {
                t.HasCheckConstraint(
                    "ck_discount_codes_window",
                    "valid_from IS NULL OR valid_until IS NULL OR valid_until > valid_from"
                );
                t.HasCheckConstraint(
                    "ck_discount_codes_max_redemptions",
                    "max_redemptions IS NULL OR max_redemptions > 0"
                );
                t.HasCheckConstraint("ck_discount_codes_duration", "duration IN (1, 2)");
            }
        );

        // BaseEntity columns
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.DeletedBy).HasColumnName("deleted_by");

        b.Property(x => x.Code).HasColumnName("code").IsRequired().HasMaxLength(32);
        b.HasIndex(x => x.Code)
            .IsUnique()
            .HasFilter("deleted_at IS NULL")
            .HasDatabaseName("ux_discount_codes_code_live");
        b.Property(x => x.Label).HasColumnName("label").HasMaxLength(120);
        b.Property(x => x.Note).HasColumnName("note").HasMaxLength(500);
        b.Property(x => x.Kind).HasColumnName("kind").IsRequired();
        b.Property(x => x.Value).HasColumnName("value").HasColumnType("numeric(12,2)").IsRequired();
        b.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3);
        b.Property(x => x.Duration)
            .HasColumnName("duration")
            .HasDefaultValue(Domain.Enums.DiscountDuration.Once);
        b.Property(x => x.ValidFrom).HasColumnName("valid_from");
        b.Property(x => x.ValidUntil).HasColumnName("valid_until");
        b.Property(x => x.MaxRedemptions).HasColumnName("max_redemptions");
        b.Property(x => x.IsActive).HasColumnName("is_active").HasDefaultValue(true);

        // The DiscountCode.PlanScopes navigation, the join table's PK/FKs and its plan_id index are
        // all configured on DiscountCodePlanMapping (the doc's task 7).
    }
}
