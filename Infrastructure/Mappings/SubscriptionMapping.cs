using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

public class SubscriptionMapping : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> b)
    {
        b.ToTable("subscriptions");

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
        b.HasIndex(x => x.OwnerId).IsUnique(); // one subscription per tenant
        b.Property(x => x.PlanId).HasColumnName("plan_id");
        b.Property(x => x.Status).HasColumnName("status");
        b.Property(x => x.BillingProvider).HasColumnName("billing_provider").HasMaxLength(128);
        b.Property(x => x.ExternalCustomerId).HasColumnName("external_customer_id").HasMaxLength(128);
        b.Property(x => x.ExternalSubscriptionId).HasColumnName("external_subscription_id").HasMaxLength(128);
        b.Property(x => x.CurrentPeriodEnd).HasColumnName("current_period_end");
        b.Property(x => x.TrialEndsAt).HasColumnName("trial_ends_at");

        b.HasOne(x => x.Plan).WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
    }
}
