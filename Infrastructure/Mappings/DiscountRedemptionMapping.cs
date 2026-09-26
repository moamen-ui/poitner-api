using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

/// <summary>DB-20 §3.4. R8.9 financial ledger table: FK SET NULL, survives the workspace. Not
/// trigger-protected (statuses move) — only <see cref="Pointer.Domain.Entity.BillingPayment"/> is
/// append-only. ck_discount_redemptions_amounts (Postgres regex `~`) is registered Npgsql-only in
/// <see cref="Pointer.Infrastructure.AppDbContext"/> — see the comment there.</summary>
public class DiscountRedemptionMapping : IEntityTypeConfiguration<DiscountRedemption>
{
    public void Configure(EntityTypeBuilder<DiscountRedemption> b)
    {
        b.ToTable(
            "discount_redemptions",
            t =>
                t.HasCheckConstraint(
                    "ck_discount_redemptions_status_shape",
                    "(status = 1 AND applied_at IS NULL AND released_at IS NULL AND release_reason IS NULL) OR (status = 2 AND applied_at IS NOT NULL AND released_at IS NULL AND release_reason IS NULL) OR (status = 3 AND released_at IS NOT NULL AND release_reason IS NOT NULL)"
                )
        );

        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();

        // R8.9 financial ledger table: owner_id carries the filter shape but the row is NOT deleted
        // with the workspace — FK SET NULL keeps it as an operator/bookkeeping record.
        b.Property(x => x.OwnerId).HasColumnName("owner_id");
        b.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(x => x.OwnerId)
            .OnDelete(DeleteBehavior.SetNull)
            .HasConstraintName("fk_discount_redemptions_workspaces_owner_id");

        b.Property(x => x.DiscountCodeId).HasColumnName("discount_code_id");
        b.HasOne<DiscountCode>()
            .WithMany()
            .HasForeignKey(x => x.DiscountCodeId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_discount_redemptions_discount_codes_discount_code_id");

        b.Property(x => x.PlanId).HasColumnName("plan_id");
        b.HasOne<Plan>()
            .WithMany()
            .HasForeignKey(x => x.PlanId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_discount_redemptions_plans_plan_id");

        b.Property(x => x.Status).HasColumnName("status").IsRequired();
        b.Property(x => x.CodeSnapshot)
            .HasColumnName("code_snapshot")
            .HasMaxLength(32)
            .IsRequired();
        b.Property(x => x.KindSnapshot).HasColumnName("kind_snapshot").IsRequired();
        b.Property(x => x.DurationSnapshot).HasColumnName("duration_snapshot").IsRequired();
        b.Property(x => x.ValueSnapshot)
            .HasColumnName("value_snapshot")
            .HasColumnType("numeric(12,2)")
            .IsRequired();
        b.Property(x => x.CurrencySnapshot).HasColumnName("currency_snapshot").HasMaxLength(3);
        b.Property(x => x.OriginalPrice)
            .HasColumnName("original_price")
            .HasColumnType("numeric(12,2)")
            .IsRequired();
        b.Property(x => x.DiscountAmount)
            .HasColumnName("discount_amount")
            .HasColumnType("numeric(12,2)")
            .IsRequired();
        b.Property(x => x.FinalPrice)
            .HasColumnName("final_price")
            .HasColumnType("numeric(12,2)")
            .IsRequired();
        b.Property(x => x.PriceCurrency)
            .HasColumnName("price_currency")
            .HasMaxLength(3)
            .IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        b.Property(x => x.CreatedBy).HasColumnName("created_by").IsRequired();
        b.Property(x => x.AppliedAt).HasColumnName("applied_at");
        b.Property(x => x.ReleasedAt).HasColumnName("released_at");
        b.Property(x => x.ReleaseReason).HasColumnName("release_reason");

        // One live use of a code per workspace; detached NULL owners are distinct (Postgres default).
        b.HasIndex(x => new { x.DiscountCodeId, x.OwnerId })
            .IsUnique()
            .HasFilter("status IN (1, 2)")
            .HasDatabaseName("ux_discount_redemptions_code_owner_open");
        // One open quote per workspace.
        b.HasIndex(x => x.OwnerId)
            .IsUnique()
            .HasFilter("status = 1")
            .HasDatabaseName("ux_discount_redemptions_owner_pending");
        b.HasIndex(x => new { x.DiscountCodeId, x.Status })
            .HasDatabaseName("ix_discount_redemptions_code_status");
    }
}
