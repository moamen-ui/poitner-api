using Pointer.Domain.Enums;

namespace Pointer.Application.Common;

/// <summary>
/// DB-20 §3.2/R20. The one implementation of billing arithmetic — period price/end and discount
/// math are never duplicated at a call site. C# <c>decimal</c> only (never <c>float</c>/<c>double</c>);
/// every stored amount rounds once, at two decimals, <see cref="Round"/>.
/// </summary>
public static class BillingMath
{
    /// <summary>Rounds to 2 decimal places, away from zero (R20) — the one rounding rule for money.</summary>
    public static decimal Round(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>§3.2: <c>plan.PriceMonthly</c> IS the price per billing interval, whatever its name
    /// says (verified against <c>landing/index.html:966-975</c>).</summary>
    public static decimal PeriodPrice(decimal planPriceMonthly) => Round(planPriceMonthly);

    /// <summary>Period length: Monthly → <c>start.AddMonths(1)</c>, Yearly → <c>start.AddYears(1)</c>,
    /// UTC. <paramref name="start"/> must already be <see cref="DateTimeKind.Utc"/>.</summary>
    public static DateTime PeriodEnd(DateTime start, BillingInterval interval)
    {
        if (start.Kind != DateTimeKind.Utc)
            throw new ArgumentException("start must be DateTimeKind.Utc (R20).", nameof(start));
        return interval switch
        {
            BillingInterval.Yearly => start.AddYears(1),
            _ => start.AddMonths(1),
        };
    }

    /// <summary>
    /// §3.2 discount rule. Percent → <c>Round(price * value / 100)</c>; Fixed → <c>Min(value, price)</c>,
    /// applicable only when <paramref name="codeCurrency"/> matches <paramref name="priceCurrency"/>
    /// (case-insensitive — callers already upper-case both). Returns 0 (not applicable) for a Fixed
    /// code with a currency mismatch — the caller decides whether that is a hard failure
    /// (<c>Billing.CodeNotForPlan</c>) or a silent no-discount, per call site.
    /// </summary>
    public static (decimal Discount, bool Applicable) Discount(
        decimal price,
        DiscountKind kind,
        decimal value,
        string? codeCurrency,
        string priceCurrency
    )
    {
        if (kind == DiscountKind.Percent)
            return (Round(price * value / 100m), true);

        // Fixed amount: only applicable when currencies match.
        if (
            codeCurrency == null
            || !string.Equals(codeCurrency, priceCurrency, StringComparison.OrdinalIgnoreCase)
        )
            return (0m, false);

        return (Round(Math.Min(value, price)), true);
    }

    /// <summary>Final = price - discount, floored at 0 (a 100%-or-more discount never goes negative).</summary>
    public static decimal FinalPrice(decimal price, decimal discount) =>
        Round(Math.Max(0m, price - discount));

    /// <summary>R20: every <c>DateTime</c> written must be <see cref="DateTimeKind.Utc"/> — Npgsql
    /// refuses <c>Unspecified</c> for <c>timestamptz</c> (the DB-15 lesson). A request DTO's
    /// <c>DateTime</c>/<c>DateTime?</c> is deserialized by <c>System.Text.Json</c> as
    /// <see cref="DateTimeKind.Unspecified"/> whenever the caller omits a timezone offset, so every
    /// entry point that copies a request timestamp onto an entity must convert it through here
    /// first — not just <c>BillingService.RecordPaymentAsync</c>'s hand-rolled <c>paidAt</c>
    /// conversion, which this mirrors.</summary>
    public static DateTime? ToUtc(DateTime? dt) =>
        dt.HasValue ? DateTime.SpecifyKind(dt.Value.ToUniversalTime(), DateTimeKind.Utc) : null;

    /// <summary>Non-nullable overload of <see cref="ToUtc(DateTime?)"/>.</summary>
    public static DateTime ToUtc(DateTime dt) =>
        DateTime.SpecifyKind(dt.ToUniversalTime(), DateTimeKind.Utc);
}
