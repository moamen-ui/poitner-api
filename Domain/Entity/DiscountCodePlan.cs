namespace Pointer.Domain.Entity;

/// <summary>
/// DB-20. Join row scoping a <see cref="DiscountCode"/> to a specific <see cref="Plan"/>. Not a
/// <see cref="BaseEntity"/> — a plain join table, like <c>Role</c>'s permission join rows. No rows
/// for a code = valid for every plan (§3.4).
/// </summary>
public class DiscountCodePlan
{
    public int DiscountCodeId { get; set; }
    public int PlanId { get; set; }
}
