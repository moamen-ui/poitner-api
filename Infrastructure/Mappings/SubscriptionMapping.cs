using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class SubscriptionMapping : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> b)
    {
        b.ToTable(
            "subscriptions",
            t =>
            {
                // DB-20 §3.3 — hold for every existing row (new columns NULL/false).
                // ck_subscriptions_quote_valid is registered in AppDbContext.OnModelCreating
                // (Npgsql-only — see the comment there: its regex `~` operator is not valid SQLite
                // grammar, unlike this file's other checks, so Sqlite-backed unit tests would fail
                // at CREATE TABLE if it were unconditional here).
                t.HasCheckConstraint(
                    "ck_subscriptions_request_consistent",
                    "(requested_plan_id IS NULL) = (requested_at IS NULL) AND (requested_plan_id IS NULL) = (requested_by IS NULL) AND (requested_plan_id IS NULL) = (quoted_price IS NULL) AND (requested_plan_id IS NULL) = (quoted_currency IS NULL)"
                );
                t.HasCheckConstraint(
                    "ck_subscriptions_comp_consistent",
                    "(is_complimentary = (comped_at IS NOT NULL)) AND ((comped_at IS NULL) = (comped_by IS NULL)) AND (is_complimentary OR (comp_reason IS NULL AND comp_ends_at IS NULL))"
                );
                t.HasCheckConstraint(
                    "ck_subscriptions_comp_no_request",
                    "NOT (is_complimentary AND requested_plan_id IS NOT NULL)"
                );
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

        // Subscription-specific columns
        b.Property(x => x.OwnerId).HasColumnName("owner_id").IsRequired(); // tenant boundary — never null
        b.HasOne<Workspace>()
            .WithMany()
            .HasForeignKey(x => x.OwnerId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_subscriptions_workspaces_owner_id");
        b.HasIndex(x => x.OwnerId)
            .IsUnique()
            .HasFilter("deleted_at IS NULL")
            .HasDatabaseName("ux_subscriptions_owner_live"); // one subscription per tenant
        b.Property(x => x.PlanId).HasColumnName("plan_id");
        b.Property(x => x.Status).HasColumnName("status");
        b.Property(x => x.BillingProvider).HasColumnName("billing_provider").HasMaxLength(128);
        b.Property(x => x.ExternalCustomerId)
            .HasColumnName("external_customer_id")
            .HasMaxLength(128);
        b.Property(x => x.ExternalSubscriptionId)
            .HasColumnName("external_subscription_id")
            .HasMaxLength(128);
        b.Property(x => x.CurrentPeriodEnd).HasColumnName("current_period_end");
        b.Property(x => x.TrialEndsAt).HasColumnName("trial_ends_at");

        b.HasOne(x => x.Plan)
            .WithMany()
            .HasForeignKey(x => x.PlanId)
            .OnDelete(DeleteBehavior.Restrict);

        // ── DB-20: the workspace's request for a paid plan ──
        b.Property(x => x.RequestedPlanId).HasColumnName("requested_plan_id");
        // No navigation added (§3.3 task 2) — configured by FK only.
        b.HasOne<Plan>()
            .WithMany()
            .HasForeignKey(x => x.RequestedPlanId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_subscriptions_plans_requested_plan_id");
        b.Property(x => x.RequestedAt).HasColumnName("requested_at");
        b.Property(x => x.RequestedBy).HasColumnName("requested_by");
        b.Property(x => x.QuotedPrice).HasColumnName("quoted_price").HasColumnType("numeric(12,2)");
        b.Property(x => x.QuotedCurrency).HasColumnName("quoted_currency").HasMaxLength(3);

        // ── DB-20: complimentary marker ──
        b.Property(x => x.IsComplimentary)
            .HasColumnName("is_complimentary")
            .HasDefaultValue(false);
        b.Property(x => x.CompedAt).HasColumnName("comped_at");
        b.Property(x => x.CompedBy).HasColumnName("comped_by");
        b.Property(x => x.CompReason).HasColumnName("comp_reason").HasMaxLength(200);
        b.Property(x => x.CompEndsAt).HasColumnName("comp_ends_at");
        b.Property(x => x.RenewalReminderSentAt).HasColumnName("renewal_reminder_sent_at");

        // The period job's own predicates (DB-17/18 precedent — partial indexes).
        b.HasIndex(x => x.CurrentPeriodEnd)
            .HasFilter("current_period_end IS NOT NULL")
            .HasDatabaseName("ix_subscriptions_current_period_end");
        b.HasIndex(x => x.CompEndsAt)
            .HasFilter("comp_ends_at IS NOT NULL")
            .HasDatabaseName("ix_subscriptions_comp_ends_at");
    }
}
