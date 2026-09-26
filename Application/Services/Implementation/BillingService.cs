using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Billing;
using Pointer.Application.DTOs.DiscountCode;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Implementation;

/// <inheritdoc cref="IBillingService" />
public class BillingService(
    IUnitOfWork unitOfWork,
    ICurrentUser currentUser,
    IMembershipService memberships,
    IEntitlementService entitlements,
    IBillingProvider billing,
    ISettingsService settings,
    IAuditWriter? audit = null
) : IBillingService
{
    private readonly IAuditWriter _audit = audit ?? NoopAuditWriter.Instance;

    // A business-rule refusal raised INSIDE an ExecuteInTransactionAsync closure (so the whole
    // transaction — including any earlier SaveChangesAsync in the same call, e.g. releasing a
    // Pending redemption before the new quote — rolls back atomically) and mapped back to the
    // right Result<T> shape by the catch at the call site.
    private enum Outcome
    {
        Conflict,
        NotFound,
        Forbidden,
        Failure,
    }

    private sealed class BillingRuleException(Outcome outcome, string message) : Exception(message)
    {
        public Outcome Outcome { get; } = outcome;
    }

    private static Result<T> MapOutcome<T>(BillingRuleException ex) =>
        ex.Outcome switch
        {
            Outcome.Conflict => Result<T>.Conflict(ex.Message),
            Outcome.NotFound => Result<T>.NotFound(ex.Message),
            Outcome.Forbidden => Result<T>.Forbidden(ex.Message),
            _ => Result<T>.Failure(ex.Message),
        };

    private static Result MapOutcome(BillingRuleException ex) =>
        ex.Outcome switch
        {
            Outcome.Conflict => Result.Conflict(ex.Message),
            Outcome.NotFound => Result.NotFound(ex.Message),
            Outcome.Forbidden => Result.Forbidden(ex.Message),
            _ => Result.Failure(ex.Message),
        };

    // ── Workspace-side gate (§3.6a: "Workspace Admin of the current workspace") ──────────────

    private async Task<Result<Guid>> RequireWorkspaceAdminAsync()
    {
        if (currentUser.Id is not Guid || currentUser.TenantId is not Guid tenantId)
            return Result<Guid>.Forbidden(MessageKeys.Common.Forbidden);

        var workspace = await unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == tenantId && w.DeletedAt == null);
        if (workspace == null)
            return Result<Guid>.Forbidden(MessageKeys.Common.Forbidden);

        if (!await WorkspaceLifecycleGuard.CanManageAsync(currentUser, memberships, workspace))
            return Result<Guid>.Forbidden(MessageKeys.Common.Forbidden);

        return Result<Guid>.Success(tenantId);
    }

    // ── Quote (§3.6 "Quote(plan, code?)") — pure, no writes ─────────────────────────────────

    private sealed record QuoteResult(
        Plan Plan,
        decimal Price,
        decimal Discount,
        decimal Final,
        string Currency,
        DiscountCode? Code
    );

    private async Task<Result<QuoteResult>> QuoteInternalAsync(
        Guid workspaceId,
        int planId,
        string? code
    )
    {
        var plan = await unitOfWork
            .Repository<Plan>()
            .Query()
            .AsNoTracking()
            .FirstOrDefaultAsync(p =>
                p.Id == planId
                && p.DeletedAt == null
                && p.IsActive
                && p.DisplayState != PlanDisplayState.Hidden
                && p.PriceMonthly > 0
            );
        if (plan == null)
            return Result<QuoteResult>.Failure(MessageKeys.Billing.PlanMisconfigured);

        var currency = plan.Currency.Trim().ToUpperInvariant();
        if (!Regex.IsMatch(currency, "^[A-Z]{3}$"))
            return Result<QuoteResult>.Failure(MessageKeys.Billing.PlanMisconfigured);

        var price = BillingMath.PeriodPrice(plan.PriceMonthly);

        if (string.IsNullOrWhiteSpace(code))
            return Result<QuoteResult>.Success(
                new QuoteResult(plan, price, 0m, price, currency, null)
            );

        var normalized = code.Trim().ToUpperInvariant();
        var dc = await unitOfWork
            .Repository<DiscountCode>()
            .Query()
            .Include(d => d.PlanScopes)
            .FirstOrDefaultAsync(d => d.Code == normalized && d.DeletedAt == null);

        var now = DateTime.UtcNow;
        var invalid =
            dc == null
            || !dc.IsActive
            || (dc.ValidFrom != null && now < dc.ValidFrom)
            || (dc.ValidUntil != null && now >= dc.ValidUntil);
        if (!invalid && dc!.MaxRedemptions != null)
        {
            var count = await unitOfWork
                .DiscountRedemptions.IgnoreQueryFilters()
                .CountAsync(r =>
                    r.DiscountCodeId == dc.Id
                    && (
                        r.Status == DiscountRedemptionStatus.Pending
                        || r.Status == DiscountRedemptionStatus.Applied
                    )
                );
            invalid = count >= dc.MaxRedemptions.Value;
        }
        if (invalid)
            return Result<QuoteResult>.Failure(MessageKeys.Billing.CodeInvalid);

        if (dc!.PlanScopes.Count > 0 && dc.PlanScopes.All(s => s.PlanId != planId))
            return Result<QuoteResult>.Failure(MessageKeys.Billing.CodeNotForPlan);

        // Review finding #3: a workspace's own Pending redemption of this code must NOT block a
        // quote — RequestPlanAsync (§3.6a) releases that same Pending redemption before re-quoting,
        // so only an already-Applied redemption of this code by this workspace is a real reuse.
        var alreadyUsed = await unitOfWork
            .DiscountRedemptions.IgnoreQueryFilters()
            .AnyAsync(r =>
                r.DiscountCodeId == dc.Id
                && r.OwnerId == workspaceId
                && r.Status == DiscountRedemptionStatus.Applied
            );
        if (alreadyUsed)
            return Result<QuoteResult>.Failure(MessageKeys.Billing.CodeAlreadyUsed);

        var (discount, applicable) = BillingMath.Discount(
            price,
            dc.Kind,
            dc.Value,
            dc.Currency,
            currency
        );
        if (!applicable)
            return Result<QuoteResult>.Failure(MessageKeys.Billing.CodeNotForPlan);

        var final = BillingMath.FinalPrice(price, discount);
        return Result<QuoteResult>.Success(
            new QuoteResult(plan, price, discount, final, currency, dc)
        );
    }

    public async Task<Result<BillingQuoteResponse>> QuoteAsync(int planId, string? referenceCode)
    {
        var gate = await RequireWorkspaceAdminAsync();
        if (!gate.IsSuccess)
            return Result<BillingQuoteResponse>.Forbidden(
                gate.Message ?? MessageKeys.Common.Forbidden
            );

        var quote = await QuoteInternalAsync(gate.Data, planId, referenceCode);
        if (!quote.IsSuccess)
            return Result<BillingQuoteResponse>.Failure(
                quote.Message ?? MessageKeys.Billing.CodeInvalid
            );

        var q = quote.Data!;
        return Result<BillingQuoteResponse>.Success(
            new BillingQuoteResponse
            {
                PlanId = q.Plan.Id,
                PlanName = q.Plan.Name,
                Price = q.Price,
                Discount = q.Discount,
                Final = q.Final,
                Currency = q.Currency,
                DiscountCodeId = q.Code?.Id,
                DiscountCodeLabel = q.Code?.Label,
            }
        );
    }

    // ── (a) Request a paid plan ──────────────────────────────────────────────────────────────

    public async Task<Result<BillingSummaryResponse>> RequestPlanAsync(
        int planId,
        string? referenceCode
    )
    {
        var gate = await RequireWorkspaceAdminAsync();
        if (!gate.IsSuccess)
            return Result<BillingSummaryResponse>.Forbidden(
                gate.Message ?? MessageKeys.Common.Forbidden
            );
        var workspaceId = gate.Data;

        Subscription? updated = null;
        int? discountCodeId = null;

        try
        {
            await unitOfWork.ExecuteInTransactionAsync(async () =>
            {
                await unitOfWork.ExecuteSqlRawAsync(
                    "SELECT id FROM workspaces WHERE id = {0} FOR UPDATE",
                    workspaceId
                );

                var sub = await unitOfWork
                    .Repository<Subscription>()
                    .Query()
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(s => s.OwnerId == workspaceId && s.DeletedAt == null);

                if (sub != null && sub.IsComplimentary)
                    throw new BillingRuleException(
                        Outcome.Conflict,
                        MessageKeys.Billing.Complimentary
                    );
                if (
                    sub != null
                    && sub.PlanId == planId
                    && sub.Status
                        is SubscriptionStatus.Active
                            or SubscriptionStatus.PastDue
                            or SubscriptionStatus.PendingActivation
                )
                    throw new BillingRuleException(
                        Outcome.Conflict,
                        MessageKeys.Billing.AlreadyOnPlan
                    );

                // Release this workspace's own Pending redemption, if any, BEFORE quoting — EF's
                // change tracker doesn't know the partial unique indexes' filters, so the release
                // must hit the database before the new insert (§3.6a).
                var ownPending = await unitOfWork
                    .DiscountRedemptions.Where(r =>
                        r.OwnerId == workspaceId && r.Status == DiscountRedemptionStatus.Pending
                    )
                    .FirstOrDefaultAsync();
                if (ownPending != null)
                {
                    ownPending.Status = DiscountRedemptionStatus.Released;
                    ownPending.ReleasedAt = DateTime.UtcNow;
                    ownPending.ReleaseReason = RedemptionReleaseReason.Replaced;
                    await unitOfWork.SaveChangesAsync();
                }

                if (!string.IsNullOrWhiteSpace(referenceCode))
                    await unitOfWork.ExecuteSqlRawAsync(
                        "SELECT id FROM discount_codes WHERE code = {0} FOR UPDATE",
                        referenceCode.Trim().ToUpperInvariant()
                    );

                var quote = await QuoteInternalAsync(workspaceId, planId, referenceCode);
                if (!quote.IsSuccess)
                    throw new BillingRuleException(
                        Outcome.Failure,
                        quote.Message ?? MessageKeys.Billing.CodeInvalid
                    );
                var q = quote.Data!;

                if (q.Code != null)
                {
                    var now = DateTime.UtcNow;
                    await unitOfWork.DiscountRedemptions.AddAsync(
                        new DiscountRedemption
                        {
                            OwnerId = workspaceId,
                            DiscountCodeId = q.Code.Id,
                            PlanId = q.Plan.Id,
                            Status = DiscountRedemptionStatus.Pending,
                            CodeSnapshot = q.Code.Code,
                            KindSnapshot = q.Code.Kind,
                            DurationSnapshot = q.Code.Duration,
                            ValueSnapshot = q.Code.Value,
                            CurrencySnapshot = q.Code.Currency,
                            OriginalPrice = q.Price,
                            DiscountAmount = q.Discount,
                            FinalPrice = q.Final,
                            PriceCurrency = q.Currency,
                            CreatedAt = now,
                            CreatedBy = currentUser.Id!.Value,
                        }
                    );
                    discountCodeId = q.Code.Id;
                }

                if (sub == null)
                {
                    sub = new Subscription
                    {
                        OwnerId = workspaceId,
                        PlanId = await entitlements.GetFreePlanIdAsync(),
                        Status = SubscriptionStatus.PendingActivation,
                    };
                    await unitOfWork.Repository<Subscription>().AddAsync(sub);
                }
                else if (sub.Status == SubscriptionStatus.None)
                {
                    sub.Status = SubscriptionStatus.PendingActivation;
                }

                sub.RequestedPlanId = planId;
                sub.RequestedAt = DateTime.UtcNow;
                sub.RequestedBy = currentUser.Id!.Value;
                sub.QuotedPrice = q.Final;
                sub.QuotedCurrency = q.Currency;

                await unitOfWork.SaveChangesAsync();

                var after = new Dictionary<string, string>
                {
                    ["plan_id"] = planId.ToString(),
                    ["amount"] = q.Final.ToString("F2"),
                    ["currency"] = q.Currency,
                };
                if (discountCodeId is int dcid)
                    after["discount_code_id"] = dcid.ToString();

                await _audit.WriteAsync(
                    new AuditEntry(
                        AuditActions.BillingPlanRequested,
                        AuditTargets.Workspace,
                        workspaceId.ToString(),
                        workspaceId,
                        After: after
                    )
                );

                updated = sub;
            });
        }
        catch (BillingRuleException ex)
        {
            return MapOutcome<BillingSummaryResponse>(ex);
        }

        return await BuildSummaryAsync(workspaceId, updated!);
    }

    // ── (b) Cancel / reject ──────────────────────────────────────────────────────────────────

    public async Task<Result> CancelRequestAsync()
    {
        var gate = await RequireWorkspaceAdminAsync();
        if (!gate.IsSuccess)
            return Result.Forbidden(gate.Message ?? MessageKeys.Common.Forbidden);
        return await ClearRequestAsync(
            gate.Data,
            RedemptionReleaseReason.CancelledByWorkspace,
            AuditActions.BillingRequestCancelled
        );
    }

    public async Task<Result> RejectRequestAsync(Guid workspaceId) =>
        await ClearRequestAsync(
            workspaceId,
            RedemptionReleaseReason.RejectedByOperator,
            AuditActions.BillingRequestRejected
        );

    private async Task<Result> ClearRequestAsync(
        Guid workspaceId,
        RedemptionReleaseReason releaseReason,
        string auditAction
    )
    {
        int? requestedPlanId = null;

        try
        {
            await unitOfWork.ExecuteInTransactionAsync(async () =>
            {
                await unitOfWork.ExecuteSqlRawAsync(
                    "SELECT id FROM workspaces WHERE id = {0} FOR UPDATE",
                    workspaceId
                );

                var sub = await unitOfWork
                    .Repository<Subscription>()
                    .Query()
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(s => s.OwnerId == workspaceId && s.DeletedAt == null);

                if (sub?.RequestedPlanId == null)
                    throw new BillingRuleException(
                        Outcome.NotFound,
                        MessageKeys.Billing.RequestNotFound
                    );

                requestedPlanId = sub.RequestedPlanId;

                var pending = await unitOfWork
                    .DiscountRedemptions.Where(r =>
                        r.OwnerId == workspaceId && r.Status == DiscountRedemptionStatus.Pending
                    )
                    .FirstOrDefaultAsync();
                if (pending != null)
                {
                    pending.Status = DiscountRedemptionStatus.Released;
                    pending.ReleasedAt = DateTime.UtcNow;
                    pending.ReleaseReason = releaseReason;
                }

                sub.RequestedPlanId = null;
                sub.RequestedAt = null;
                sub.RequestedBy = null;
                sub.QuotedPrice = null;
                sub.QuotedCurrency = null;
                if (
                    sub.Status == SubscriptionStatus.PendingActivation
                    && sub.PlanId == await entitlements.GetFreePlanIdAsync()
                )
                    sub.Status = SubscriptionStatus.None;

                await unitOfWork.SaveChangesAsync();

                await _audit.WriteAsync(
                    new AuditEntry(
                        auditAction,
                        AuditTargets.Workspace,
                        workspaceId.ToString(),
                        workspaceId,
                        After: new Dictionary<string, string>
                        {
                            ["plan_id"] = requestedPlanId!.ToString()!,
                        }
                    )
                );
            });
        }
        catch (BillingRuleException ex)
        {
            return MapOutcome(ex);
        }

        return Result.Success();
    }

    // ── (c) Record a payment ─────────────────────────────────────────────────────────────────

    public async Task<Result<OperatorPaymentResponse>> RecordPaymentAsync(
        Guid workspaceId,
        decimal amount,
        string? currency,
        DateTime paidAt,
        PaymentMethod method,
        string? reference,
        string? note
    )
    {
        BillingPayment? inserted = null;

        try
        {
            await unitOfWork.ExecuteInTransactionAsync(async () =>
            {
                await unitOfWork.ExecuteSqlRawAsync(
                    "SELECT id FROM workspaces WHERE id = {0} FOR UPDATE",
                    workspaceId
                );

                var sub = await unitOfWork
                    .Repository<Subscription>()
                    .Query()
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(s => s.OwnerId == workspaceId && s.DeletedAt == null);
                if (sub == null)
                    throw new BillingRuleException(
                        Outcome.NotFound,
                        MessageKeys.Billing.NothingToPay
                    );
                if (sub.IsComplimentary)
                    throw new BillingRuleException(
                        Outcome.Conflict,
                        MessageKeys.Billing.Complimentary
                    );

                var targetPlanId = sub.RequestedPlanId ?? sub.PlanId;
                var target = await unitOfWork
                    .Repository<Plan>()
                    .Query()
                    .FirstOrDefaultAsync(p => p.Id == targetPlanId && p.DeletedAt == null);
                if (target == null || target.PriceMonthly <= 0)
                    throw new BillingRuleException(
                        Outcome.Conflict,
                        MessageKeys.Billing.NothingToPay
                    );

                var targetCurrency = target.Currency.Trim().ToUpperInvariant();
                decimal quoted;
                DiscountRedemption? redemptionToApply = null;
                var discountFirstApplied = false;

                if (sub.RequestedPlanId != null)
                {
                    quoted = sub.QuotedPrice ?? BillingMath.PeriodPrice(target.PriceMonthly);
                    redemptionToApply = await unitOfWork
                        .DiscountRedemptions.Where(r =>
                            r.OwnerId == workspaceId && r.Status == DiscountRedemptionStatus.Pending
                        )
                        .FirstOrDefaultAsync();
                    discountFirstApplied = redemptionToApply != null;
                }
                else
                {
                    var price = BillingMath.PeriodPrice(target.PriceMonthly);
                    var forever = await unitOfWork
                        .DiscountRedemptions.Where(r =>
                            r.OwnerId == workspaceId
                            && r.PlanId == target.Id
                            && r.Status == DiscountRedemptionStatus.Applied
                            && r.DurationSnapshot == DiscountDuration.Forever
                        )
                        .OrderByDescending(r => r.Id)
                        .FirstOrDefaultAsync();
                    if (forever != null)
                    {
                        var (discount, applicable) = BillingMath.Discount(
                            price,
                            forever.KindSnapshot,
                            forever.ValueSnapshot,
                            forever.CurrencySnapshot,
                            targetCurrency
                        );
                        quoted = applicable ? BillingMath.FinalPrice(price, discount) : price;
                        if (applicable)
                            redemptionToApply = forever; // re-used, not first-applied
                    }
                    else
                    {
                        quoted = price;
                    }
                }

                var effectiveCurrency = (currency ?? targetCurrency).Trim().ToUpperInvariant();
                if (!Regex.IsMatch(effectiveCurrency, "^[A-Z]{3}$"))
                    throw new BillingRuleException(
                        Outcome.Failure,
                        MessageKeys.Billing.InvalidAmount
                    );
                if (amount < 0 || amount > 9_999_999_999.99m)
                    throw new BillingRuleException(
                        Outcome.Failure,
                        MessageKeys.Billing.InvalidAmount
                    );
                var now = DateTime.UtcNow;
                var paidAtUtc = DateTime.SpecifyKind(paidAt.ToUniversalTime(), DateTimeKind.Utc);
                if (paidAtUtc < now.AddDays(-366) || paidAtUtc > now.AddMinutes(5))
                    throw new BillingRuleException(
                        Outcome.Failure,
                        MessageKeys.Billing.InvalidPaidAt
                    );
                if ((reference?.Length ?? 0) > 128 || (note?.Length ?? 0) > 500)
                    throw new BillingRuleException(
                        Outcome.Failure,
                        MessageKeys.Billing.InvalidAmount
                    );

                // F-B4: first payment / plan change starts NOW; a contiguous renewal starts at the
                // old period end (late payers pay for the grace days; early renewals stack).
                var isRenewal =
                    sub.RequestedPlanId == null
                    && sub.Status is SubscriptionStatus.Active or SubscriptionStatus.PastDue
                    && sub.CurrentPeriodEnd != null;
                var start = isRenewal ? sub.CurrentPeriodEnd!.Value : now;
                var end = BillingMath.PeriodEnd(start, target.Interval);

                var payment = new BillingPayment
                {
                    OwnerId = workspaceId,
                    PlanId = target.Id,
                    Kind = BillingPaymentKind.Payment,
                    Amount = BillingMath.Round(amount),
                    Currency = effectiveCurrency,
                    QuotedAmount = quoted,
                    Method = method,
                    Reference = reference,
                    Note = note,
                    PaidAt = paidAtUtc,
                    PeriodStart = start,
                    PeriodEnd = end,
                    PreviousPlanId = sub.PlanId,
                    PreviousStatus = sub.Status,
                    PreviousPeriodEnd = sub.CurrentPeriodEnd,
                    DiscountRedemptionId = redemptionToApply?.Id,
                    DiscountFirstApplied = discountFirstApplied,
                    RecordedAt = now,
                    RecordedBy = currentUser.Id!.Value,
                };
                await unitOfWork.BillingPayments.AddAsync(payment);

                if (redemptionToApply != null && discountFirstApplied)
                {
                    redemptionToApply.Status = DiscountRedemptionStatus.Applied;
                    redemptionToApply.AppliedAt = now;
                }

                var planChanged = sub.PlanId != target.Id;
                sub.PlanId = target.Id;
                sub.Status = SubscriptionStatus.Active;
                sub.CurrentPeriodEnd = end;
                sub.RenewalReminderSentAt = null;
                sub.BillingProvider = "manual";
                sub.RequestedPlanId = null;
                sub.RequestedAt = null;
                sub.RequestedBy = null;
                sub.QuotedPrice = null;
                sub.QuotedCurrency = null;

                if (planChanged)
                    await billing.ChangePlanAsync(sub, target.Id);
                await billing.ActivateAsync(sub);

                await unitOfWork.SaveChangesAsync();
                inserted = payment;

                var after = new Dictionary<string, string>
                {
                    ["payment_id"] = payment.Id.ToString(),
                    ["plan_id"] = target.Id.ToString(),
                    ["amount"] = payment.Amount.ToString("F2"),
                    ["currency"] = payment.Currency,
                    ["method"] = method.ToString(),
                    ["period_end"] = end.ToString("O"),
                };
                if (payment.DiscountRedemptionId is long rid)
                    after["discount_code_id"] = redemptionToApply!.DiscountCodeId.ToString();

                await _audit.WriteAsync(
                    new AuditEntry(
                        AuditActions.BillingPaymentRecorded,
                        AuditTargets.Workspace,
                        workspaceId.ToString(),
                        workspaceId,
                        After: after
                    )
                );
            });
        }
        catch (BillingRuleException ex)
        {
            return MapOutcome<OperatorPaymentResponse>(ex);
        }

        return Result<OperatorPaymentResponse>.Success(await ToOperatorResponseAsync(inserted!));
    }

    // ── (d) Void ──────────────────────────────────────────────────────────────────────────────

    public async Task<Result> VoidPaymentAsync(Guid workspaceId, long paymentId, string reason)
    {
        try
        {
            await unitOfWork.ExecuteInTransactionAsync(async () =>
            {
                await unitOfWork.ExecuteSqlRawAsync(
                    "SELECT id FROM workspaces WHERE id = {0} FOR UPDATE",
                    workspaceId
                );

                var payment = await unitOfWork.BillingPayments.FirstOrDefaultAsync(p =>
                    p.Id == paymentId
                    && p.OwnerId == workspaceId
                    && p.Kind == BillingPaymentKind.Payment
                );
                if (payment == null)
                    throw new BillingRuleException(
                        Outcome.NotFound,
                        MessageKeys.Billing.NothingToPay
                    );

                var alreadyVoided = await unitOfWork.BillingPayments.AnyAsync(p =>
                    p.VoidsPaymentId == payment.Id
                );
                if (alreadyVoided)
                    throw new BillingRuleException(
                        Outcome.Conflict,
                        MessageKeys.Billing.VoidOnlyLatest
                    );

                // Review finding #1: a voided Payment row keeps Kind == Payment forever (the ledger
                // is append-only), so "latest Payment" must exclude any Payment this workspace has
                // already voided — otherwise once the newest payment is voided, the next-newest can
                // never be voided (it is permanently shadowed by the already-voided one).
                var voidedPaymentIds = unitOfWork
                    .BillingPayments.Where(v =>
                        v.OwnerId == workspaceId
                        && v.Kind == BillingPaymentKind.Void
                        && v.VoidsPaymentId != null
                    )
                    .Select(v => v.VoidsPaymentId!.Value);

                var latestId = await unitOfWork
                    .BillingPayments.Where(p =>
                        p.OwnerId == workspaceId
                        && p.Kind == BillingPaymentKind.Payment
                        && !voidedPaymentIds.Contains(p.Id)
                    )
                    .OrderByDescending(p => p.RecordedAt)
                    .ThenByDescending(p => p.Id)
                    .Select(p => p.Id)
                    .FirstOrDefaultAsync();
                if (latestId != payment.Id)
                    throw new BillingRuleException(
                        Outcome.Conflict,
                        MessageKeys.Billing.VoidOnlyLatest
                    );

                var sub = await unitOfWork
                    .Repository<Subscription>()
                    .Query()
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(s => s.OwnerId == workspaceId && s.DeletedAt == null);
                if (sub == null)
                    throw new BillingRuleException(
                        Outcome.NotFound,
                        MessageKeys.Billing.NothingToPay
                    );

                var now = DateTime.UtcNow;
                var voidRow = new BillingPayment
                {
                    OwnerId = workspaceId,
                    PlanId = payment.PlanId,
                    Kind = BillingPaymentKind.Void,
                    Amount = payment.Amount,
                    Currency = payment.Currency,
                    Note = reason,
                    VoidsPaymentId = payment.Id,
                    PreviousPlanId = sub.PlanId,
                    PreviousStatus = sub.Status,
                    PreviousPeriodEnd = sub.CurrentPeriodEnd,
                    RecordedAt = now,
                    RecordedBy = currentUser.Id!.Value,
                };
                await unitOfWork.BillingPayments.AddAsync(voidRow);

                var restoredPlanId = payment.PreviousPlanId!.Value;
                var restoredStatus = payment.PreviousStatus!.Value;
                var freeId = await entitlements.GetFreePlanIdAsync();
                if (
                    restoredStatus == SubscriptionStatus.PendingActivation
                    && restoredPlanId == freeId
                )
                    restoredStatus = SubscriptionStatus.None;

                sub.PlanId = restoredPlanId;
                sub.Status = restoredStatus;
                sub.CurrentPeriodEnd = payment.PreviousPeriodEnd;

                if (payment.DiscountFirstApplied && payment.DiscountRedemptionId is long rid)
                {
                    var redemption = await unitOfWork.DiscountRedemptions.FirstOrDefaultAsync(r =>
                        r.Id == rid
                    );
                    if (redemption != null)
                    {
                        redemption.Status = DiscountRedemptionStatus.Released;
                        redemption.ReleasedAt = now;
                        redemption.ReleaseReason = RedemptionReleaseReason.PaymentVoided;
                    }
                }

                await unitOfWork.SaveChangesAsync();

                await _audit.WriteAsync(
                    new AuditEntry(
                        AuditActions.BillingPaymentVoided,
                        AuditTargets.Workspace,
                        workspaceId.ToString(),
                        workspaceId,
                        After: new Dictionary<string, string>
                        {
                            ["payment_id"] = payment.Id.ToString(),
                        }
                    )
                );
            });
        }
        catch (BillingRuleException ex)
        {
            return MapOutcome(ex);
        }

        return Result.Success();
    }

    // ── End comp (operator) ──────────────────────────────────────────────────────────────────

    public async Task<Result> EndCompAsync(Guid workspaceId)
    {
        try
        {
            await unitOfWork.ExecuteInTransactionAsync(async () =>
            {
                await unitOfWork.ExecuteSqlRawAsync(
                    "SELECT id FROM workspaces WHERE id = {0} FOR UPDATE",
                    workspaceId
                );

                var sub = await unitOfWork
                    .Repository<Subscription>()
                    .Query()
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(s => s.OwnerId == workspaceId && s.DeletedAt == null);
                if (sub is not { IsComplimentary: true })
                    throw new BillingRuleException(
                        Outcome.Conflict,
                        MessageKeys.Billing.StateChanged
                    );

                var now = DateTime.UtcNow;
                sub.IsComplimentary = false;
                sub.CompedAt = null;
                sub.CompedBy = null;
                sub.CompReason = null;
                sub.CompEndsAt = null;
                sub.CurrentPeriodEnd = now;
                sub.Status = SubscriptionStatus.PastDue;
                sub.RenewalReminderSentAt = null;

                await unitOfWork.SaveChangesAsync();

                await _audit.WriteAsync(
                    new AuditEntry(
                        AuditActions.BillingCompEnded,
                        AuditTargets.Workspace,
                        workspaceId.ToString(),
                        workspaceId,
                        After: new Dictionary<string, string> { ["source"] = "operator" }
                    )
                );
            });
        }
        catch (BillingRuleException ex)
        {
            return MapOutcome(ex);
        }

        return Result.Success();
    }

    // ── Reads ─────────────────────────────────────────────────────────────────────────────────

    public async Task<Result<BillingSummaryResponse>> GetSummaryAsync()
    {
        var gate = await RequireWorkspaceAdminAsync();
        if (!gate.IsSuccess)
            return Result<BillingSummaryResponse>.Forbidden(
                gate.Message ?? MessageKeys.Common.Forbidden
            );
        return await BuildSummaryAsync(gate.Data, null);
    }

    public async Task<Result<List<WorkspacePaymentResponse>>> GetWorkspacePaymentsAsync()
    {
        var gate = await RequireWorkspaceAdminAsync();
        if (!gate.IsSuccess)
            return Result<List<WorkspacePaymentResponse>>.Forbidden(
                gate.Message ?? MessageKeys.Common.Forbidden
            );

        var rows = await unitOfWork
            .BillingPayments.Where(p => p.OwnerId == gate.Data)
            .OrderByDescending(p => p.RecordedAt)
            .ThenByDescending(p => p.Id)
            .ToListAsync();
        var planNames = await PlanNameMapAsync(rows.Select(r => r.PlanId));

        return Result<List<WorkspacePaymentResponse>>.Success(
            rows.Select(p => new WorkspacePaymentResponse
                {
                    Id = p.Id,
                    Kind = p.Kind.ToString(),
                    PlanId = p.PlanId,
                    PlanName = planNames.GetValueOrDefault(p.PlanId, string.Empty),
                    Amount = p.Amount,
                    Currency = p.Currency,
                    Method = p.Method?.ToString(),
                    Reference = p.Reference,
                    PaidAt = p.PaidAt,
                    PeriodStart = p.PeriodStart,
                    PeriodEnd = p.PeriodEnd,
                })
                .ToList()
        );
    }

    // ── Requestable plans (dashboard gap: a Workspace Admin has no id-carrying plan list) ──────

    public async Task<Result<List<BillablePlanResponse>>> ListRequestablePlansAsync()
    {
        var gate = await RequireWorkspaceAdminAsync();
        if (!gate.IsSuccess)
            return Result<List<BillablePlanResponse>>.Forbidden(
                gate.Message ?? MessageKeys.Common.Forbidden
            );

        var sub = await unitOfWork
            .Repository<Subscription>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.OwnerId == gate.Data && s.DeletedAt == null);

        // Same shape as QuoteInternalAsync's own Plan lookup (§3.6 step 1) — a plan this endpoint
        // lists is exactly one /quote and /request would themselves accept for this workspace.
        var plans = await unitOfWork
            .Repository<Plan>()
            .Query()
            .AsNoTracking()
            .Where(p =>
                p.DeletedAt == null
                && p.IsActive
                && p.DisplayState != PlanDisplayState.Hidden
                && p.PriceMonthly > 0
            )
            .OrderBy(p => p.SortOrder)
            .ToListAsync();

        return Result<List<BillablePlanResponse>>.Success(
            plans
                .Select(p => new BillablePlanResponse
                {
                    Id = p.Id,
                    Slug = p.Slug,
                    Name = p.Name,
                    Price = BillingMath.PeriodPrice(p.PriceMonthly),
                    Currency = p.Currency.Trim().ToUpperInvariant(),
                    Interval = p.Interval.ToString(),
                    FeatureBullets = p.FeatureBullets,
                    IsCurrent = sub != null && sub.PlanId == p.Id,
                })
                .ToList()
        );
    }

    public async Task<Result<OperatorBillingResponse>> GetOperatorBillingAsync(Guid workspaceId)
    {
        var exists = await unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .AnyAsync(w => w.Id == workspaceId);
        if (!exists)
            return Result<OperatorBillingResponse>.NotFound("Tenant not found.");

        var summary = await BuildSummaryAsync(workspaceId, null);
        if (!summary.IsSuccess)
            return Result<OperatorBillingResponse>.Failure(
                summary.Message ?? MessageKeys.Common.NotFound
            );

        var payments = await unitOfWork
            .BillingPayments.IgnoreQueryFilters()
            .Where(p => p.OwnerId == workspaceId)
            .OrderByDescending(p => p.RecordedAt)
            .ThenByDescending(p => p.Id)
            .ToListAsync();
        var planNames = await PlanNameMapAsync(payments.Select(p => p.PlanId));

        var redemptions = await unitOfWork
            .DiscountRedemptions.IgnoreQueryFilters()
            .Where(r => r.OwnerId == workspaceId)
            .OrderByDescending(r => r.Id)
            .ToListAsync();
        var rPlanNames = await PlanNameMapAsync(redemptions.Select(r => r.PlanId));

        return Result<OperatorBillingResponse>.Success(
            new OperatorBillingResponse
            {
                Summary = summary.Data!,
                Payments = payments
                    .Select(p => new OperatorPaymentResponse
                    {
                        Id = p.Id,
                        Kind = p.Kind.ToString(),
                        PlanId = p.PlanId,
                        PlanName = planNames.GetValueOrDefault(p.PlanId, string.Empty),
                        Amount = p.Amount,
                        Currency = p.Currency,
                        QuotedAmount = p.QuotedAmount,
                        Method = p.Method?.ToString(),
                        Reference = p.Reference,
                        Note = p.Note,
                        PaidAt = p.PaidAt,
                        PeriodStart = p.PeriodStart,
                        PeriodEnd = p.PeriodEnd,
                        PreviousPlanId = p.PreviousPlanId,
                        PreviousStatus = p.PreviousStatus?.ToString(),
                        PreviousPeriodEnd = p.PreviousPeriodEnd,
                        DiscountRedemptionId = p.DiscountRedemptionId,
                        DiscountFirstApplied = p.DiscountFirstApplied,
                        VoidsPaymentId = p.VoidsPaymentId,
                        RecordedAt = p.RecordedAt,
                        RecordedBy = p.RecordedBy,
                    })
                    .ToList(),
                Redemptions = redemptions
                    .Select(r => new DiscountRedemptionResponse
                    {
                        Id = r.Id,
                        WorkspaceId = r.OwnerId,
                        WorkspaceName = "This workspace",
                        PlanId = r.PlanId,
                        PlanName = rPlanNames.GetValueOrDefault(r.PlanId, string.Empty),
                        Status = r.Status.ToString(),
                        CodeSnapshot = r.CodeSnapshot,
                        KindSnapshot = r.KindSnapshot.ToString(),
                        DurationSnapshot = r.DurationSnapshot.ToString(),
                        ValueSnapshot = r.ValueSnapshot,
                        CurrencySnapshot = r.CurrencySnapshot,
                        OriginalPrice = r.OriginalPrice,
                        DiscountAmount = r.DiscountAmount,
                        FinalPrice = r.FinalPrice,
                        PriceCurrency = r.PriceCurrency,
                        CreatedAt = r.CreatedAt,
                        AppliedAt = r.AppliedAt,
                        ReleasedAt = r.ReleasedAt,
                        ReleaseReason = r.ReleaseReason?.ToString(),
                    })
                    .ToList(),
            }
        );
    }

    // ── Shared projections ───────────────────────────────────────────────────────────────────

    private async Task<Dictionary<int, string>> PlanNameMapAsync(IEnumerable<int> ids)
    {
        var distinct = ids.Distinct().ToList();
        if (distinct.Count == 0)
            return new Dictionary<int, string>();
        return await unitOfWork
            .Repository<Plan>()
            .Query()
            .AsNoTracking()
            .Where(p => distinct.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name);
    }

    private async Task<OperatorPaymentResponse> ToOperatorResponseAsync(BillingPayment p)
    {
        var planNames = await PlanNameMapAsync(new[] { p.PlanId });
        return new OperatorPaymentResponse
        {
            Id = p.Id,
            Kind = p.Kind.ToString(),
            PlanId = p.PlanId,
            PlanName = planNames.GetValueOrDefault(p.PlanId, string.Empty),
            Amount = p.Amount,
            Currency = p.Currency,
            QuotedAmount = p.QuotedAmount,
            Method = p.Method?.ToString(),
            Reference = p.Reference,
            Note = p.Note,
            PaidAt = p.PaidAt,
            PeriodStart = p.PeriodStart,
            PeriodEnd = p.PeriodEnd,
            PreviousPlanId = p.PreviousPlanId,
            PreviousStatus = p.PreviousStatus?.ToString(),
            PreviousPeriodEnd = p.PreviousPeriodEnd,
            DiscountRedemptionId = p.DiscountRedemptionId,
            DiscountFirstApplied = p.DiscountFirstApplied,
            VoidsPaymentId = p.VoidsPaymentId,
            RecordedAt = p.RecordedAt,
            RecordedBy = p.RecordedBy,
        };
    }

    /// <summary>Builds the summary DTO for a workspace. <paramref name="tracked"/> is the
    /// just-mutated subscription when called right after a write (avoids a redundant reload);
    /// null re-loads it read-only.</summary>
    private async Task<Result<BillingSummaryResponse>> BuildSummaryAsync(
        Guid workspaceId,
        Subscription? tracked
    )
    {
        var sub =
            tracked
            ?? await unitOfWork
                .Repository<Subscription>()
                .Query()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.OwnerId == workspaceId && s.DeletedAt == null);

        int planId;
        SubscriptionStatus status;
        DateTime? currentPeriodEnd;
        bool isComplimentary;
        DateTime? compEndsAt;
        int? requestedPlanId;
        DateTime? requestedAt;
        decimal? quotedPrice;
        string? quotedCurrency;

        if (sub == null)
        {
            planId = await entitlements.GetFreePlanIdAsync();
            status = SubscriptionStatus.None;
            currentPeriodEnd = null;
            isComplimentary = false;
            compEndsAt = null;
            requestedPlanId = null;
            requestedAt = null;
            quotedPrice = null;
            quotedCurrency = null;
        }
        else
        {
            planId = sub.PlanId;
            status = sub.Status;
            currentPeriodEnd = sub.CurrentPeriodEnd;
            isComplimentary = sub.IsComplimentary;
            compEndsAt = sub.CompEndsAt;
            requestedPlanId = sub.RequestedPlanId;
            requestedAt = sub.RequestedAt;
            quotedPrice = sub.QuotedPrice;
            quotedCurrency = sub.QuotedCurrency;
        }

        var plan = await unitOfWork
            .Repository<Plan>()
            .Query()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == planId);
        var requestedPlan =
            requestedPlanId != null
                ? await unitOfWork
                    .Repository<Plan>()
                    .Query()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(p => p.Id == requestedPlanId)
                : null;

        decimal? renewalPrice = null;
        string? renewalCurrency = null;
        if (
            status == SubscriptionStatus.Active
            && !isComplimentary
            && plan is { PriceMonthly: > 0 }
        )
        {
            renewalCurrency = plan.Currency.Trim().ToUpperInvariant();
            var price = BillingMath.PeriodPrice(plan.PriceMonthly);
            var forever = await unitOfWork
                .DiscountRedemptions.IgnoreQueryFilters()
                .Where(r =>
                    r.OwnerId == workspaceId
                    && r.PlanId == plan.Id
                    && r.Status == DiscountRedemptionStatus.Applied
                    && r.DurationSnapshot == DiscountDuration.Forever
                )
                .OrderByDescending(r => r.Id)
                .FirstOrDefaultAsync();
            if (forever != null)
            {
                var (discount, applicable) = BillingMath.Discount(
                    price,
                    forever.KindSnapshot,
                    forever.ValueSnapshot,
                    forever.CurrencySnapshot,
                    renewalCurrency
                );
                renewalPrice = applicable ? BillingMath.FinalPrice(price, discount) : price;
            }
            else
            {
                renewalPrice = price;
            }
        }

        DateTime? graceEndsAt = null;
        if (status == SubscriptionStatus.PastDue && currentPeriodEnd != null)
        {
            var graceDays = await settings.GetIntAsync(ISettingsService.BillingGraceDays, 7);
            graceEndsAt = currentPeriodEnd.Value.AddDays(graceDays);
        }

        return Result<BillingSummaryResponse>.Success(
            new BillingSummaryResponse
            {
                PlanId = planId,
                PlanName = plan?.Name ?? string.Empty,
                Status = status.ToString(),
                CurrentPeriodEnd = currentPeriodEnd,
                IsComplimentary = isComplimentary,
                CompEndsAt = compEndsAt,
                RequestedPlanId = requestedPlanId,
                RequestedPlanName = requestedPlan?.Name,
                RequestedAt = requestedAt,
                QuotedPrice = quotedPrice,
                QuotedCurrency = quotedCurrency,
                RenewalPrice = renewalPrice,
                RenewalCurrency = renewalCurrency,
                GraceEndsAt = graceEndsAt,
            }
        );
    }
}
