using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Mappings;

/// <summary>DB-20 §3.4. Plain join row (not <c>BaseEntity</c>) scoping a code to a plan. No rows for
/// a code = valid for every plan.</summary>
public class DiscountCodePlanMapping : IEntityTypeConfiguration<DiscountCodePlan>
{
    public void Configure(EntityTypeBuilder<DiscountCodePlan> b)
    {
        b.ToTable("discount_code_plans");

        b.HasKey(x => new { x.DiscountCodeId, x.PlanId }).HasName("pk_discount_code_plans");

        b.Property(x => x.DiscountCodeId).HasColumnName("discount_code_id");
        b.Property(x => x.PlanId).HasColumnName("plan_id");

        b.HasOne<DiscountCode>()
            .WithMany(c => c.PlanScopes)
            .HasForeignKey(x => x.DiscountCodeId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("fk_discount_code_plans_discount_codes_discount_code_id");

        b.HasOne<Plan>()
            .WithMany()
            .HasForeignKey(x => x.PlanId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_discount_code_plans_plans_plan_id");

        b.HasIndex(x => x.PlanId);
    }
}
