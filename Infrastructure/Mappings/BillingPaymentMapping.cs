using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

/// <summary>DB-20 §3.4. R8.9 financial ledger table: FK SET NULL, survives the workspace. Append-only
/// at three layers (R17, the second such table after <see cref="AuditEvent"/>) — the Postgres trigger
/// is <c>Infrastructure/Migrations/*_AddBillingPaymentsAppendOnlyTrigger.cs</c> (NOT modelled here, same
/// as AuditEventMapping's own trigger — do not "repair" the snapshot to match it, R9).
/// ck_billing_payments_currency (Postgres regex `~`) is registered Npgsql-only in
/// <see cref="Pointer.Infrastructure.AppDbContext"/> — see the comment there.</summary>
public class BillingPaymentMapping : IEntityTypeConfiguration<BillingPayment>
{
    public void Configure(EntityTypeBuilder<BillingPayment> b)
    {
        b.ToTable(
            "billing_payments",
            t =>
            {
                t.HasCheckConstraint(
                    "ck_billing_payments_kind_shape",
                    "(kind = 1 AND method IN (1, 2, 3) AND paid_at IS NOT NULL AND period_start IS NOT NULL AND period_end IS NOT NULL AND period_end > period_start AND previous_plan_id IS NOT NULL AND previous_status IS NOT NULL AND voids_payment_id IS NULL) OR (kind = 2 AND voids_payment_id IS NOT NULL AND method IS NULL AND paid_at IS NULL AND period_start IS NULL AND period_end IS NULL AND discount_redemption_id IS NULL AND NOT discount_first_applied)"
                );
                t.HasCheckConstraint(
                    "ck_billing_payments_amounts",
                    "amount >= 0 AND (quoted_amount IS NULL OR quoted_amount >= 0)"
                );
                t.HasCheckConstraint(
                    "ck_billing_payments_first_applied",
                    "NOT discount_first_applied OR discount_redemption_id IS NOT NULL"
                );
            }
        );

        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();

        b.Property(x => x.OwnerId).HasColumnName("owner_id");
        b.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(x => x.OwnerId)
            .OnDelete(DeleteBehavior.SetNull)
            .HasConstraintName("fk_billing_payments_workspaces_owner_id");

        b.Property(x => x.PlanId).HasColumnName("plan_id");
        b.HasOne<Plan>()
            .WithMany()
            .HasForeignKey(x => x.PlanId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_billing_payments_plans_plan_id");

        b.Property(x => x.Kind).HasColumnName("kind").IsRequired();
        b.Property(x => x.Amount)
            .HasColumnName("amount")
            .HasColumnType("numeric(12,2)")
            .IsRequired();
        b.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
        b.Property(x => x.QuotedAmount)
            .HasColumnName("quoted_amount")
            .HasColumnType("numeric(12,2)");
        b.Property(x => x.Method).HasColumnName("method");
        b.Property(x => x.Reference).HasColumnName("reference").HasMaxLength(128);
        b.Property(x => x.Note).HasColumnName("note").HasMaxLength(500);
        b.Property(x => x.PaidAt).HasColumnName("paid_at");
        b.Property(x => x.PeriodStart).HasColumnName("period_start");
        b.Property(x => x.PeriodEnd).HasColumnName("period_end");

        b.Property(x => x.PreviousPlanId).HasColumnName("previous_plan_id");
        b.HasOne<Plan>()
            .WithMany()
            .HasForeignKey(x => x.PreviousPlanId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_billing_payments_plans_previous_plan_id");
        b.Property(x => x.PreviousStatus).HasColumnName("previous_status");
        b.Property(x => x.PreviousPeriodEnd).HasColumnName("previous_period_end");

        b.Property(x => x.DiscountRedemptionId).HasColumnName("discount_redemption_id");
        b.HasOne<DiscountRedemption>()
            .WithMany()
            .HasForeignKey(x => x.DiscountRedemptionId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_billing_payments_discount_redemptions_discount_redemption_id");
        b.Property(x => x.DiscountFirstApplied)
            .HasColumnName("discount_first_applied")
            .HasDefaultValue(false);

        b.Property(x => x.VoidsPaymentId).HasColumnName("voids_payment_id");
        b.HasOne<BillingPayment>()
            .WithMany()
            .HasForeignKey(x => x.VoidsPaymentId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_billing_payments_billing_payments_voids_payment_id");

        b.Property(x => x.RecordedAt).HasColumnName("recorded_at").IsRequired();
        b.Property(x => x.RecordedBy).HasColumnName("recorded_by").IsRequired();

        b.HasIndex(x => new { x.OwnerId, x.RecordedAt })
            .HasDatabaseName("ix_billing_payments_owner_recorded")
            .IsDescending(false, true);
        b.HasIndex(x => x.VoidsPaymentId)
            .IsUnique()
            .HasFilter("voids_payment_id IS NOT NULL")
            .HasDatabaseName("ux_billing_payments_voids_payment_id");
    }
}
