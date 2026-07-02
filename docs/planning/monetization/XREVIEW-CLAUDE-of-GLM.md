# Cross-review: Claude's review of PLAN-GLM.md + merge decisions

Overall: PLAN-GLM is strong and ~90% aligned with PLAN-CLAUDE. Same load-bearing decisions (Plan global
/ Subscription strict-own; reusable IEntitlementService; grandfather-by-count; public endpoint + landing
fetch; signup selector; IBillingProvider+Noop; super-admin CRUD; additive migration). Differences below,
with the decision for the merged plan.

## Adopt from GLM (better than mine)
1. **Enforcement kill-switch (`EnforcementEnabled` setting, default OFF).** GLM's R5 rollout: ship
   model+CRUD+landing first, flip enforcement on after verifying existing tenants' counts. Cleaner + safer
   than my "Legacy unlimited plan backfill." **Merged decision:** enforcement gated by
   `ISettingsService.EnforcementEnabled` (off at launch). Turn-on strategy = set generous Free defaults
   that exceed current usage OR (optional) a one-time Legacy-plan backfill for pre-migration tenants. Keep
   my Legacy idea only as an optional turn-on tool, not the primary mechanism.
2. **`IsLimitReached` Result flag + `PlanLimit{lever,current,limit,planId}` record.** Deterministic
   upgrade UX (no message string-matching). **Adopt.** (One envelope field; orval propagates it.)
3. **`ExtensionSite` entity + `POST /api/extension/activate` guard (P2), enforced-but-inert until the real
   extension calls it.** Concretely solves `maxExtensionSites`, which I under-specified. **Adopt.**
4. **`SubscriptionStatus.PendingActivation`** for paid-signup-before-activation. **Adopt.**
5. **Seed Free in `AdminSeeder` (idempotent, reads live AppSettings)** rather than raw SQL in the
   migration. **Adopt.** No `Subscription` backfill needed — missing row == Free (adopt; pairs with the
   kill-switch).
6. Operational detail: jsonb value-comparers on the list/dict columns; controller/orval conventions;
   detailed file map. **Adopt.**

## Resolve the one real divergence — entitlement storage
- Mine: typed VO (named C# props). GLM: `Dictionary<string,string>` JSONB + `EntitlementKeys` registry +
  typed accessors that fall back to the spec default on missing/malformed.
- **Decision: GLM's dict + registry + typed accessors.** Decisive reason: with a typed VO, a missing JSON
  field deserializes to `default(int)=0` → could mean "0 allowed" = accidental lockout; GLM's registry
  default (e.g. `-1`/spec default) is the safe fallback. The registry is also the single metadata source
  (type + enforced-flag + default) that the validator, enforcement, landing, and seeder all read — which
  is exactly the "single source" my plan wanted (I'd have needed a parallel catalog anyway). Enforcement
  still never touches raw strings (typed getters). Adopt GLM's.

## Keep from mine (equal or clearer)
- Free-plan seeding intent + the "current caps seed Free where a real analogue exists, sensible defaults
  otherwise" reading (GLM's R4 agrees; existing AppSettings are demo/email-scoped, so most Free values are
  fresh defaults — both plans converge).
- Public DTO = marketing fields only (name/price/currency/interval/bullets/displayState/sortOrder); NO
  entitlement values or ids beyond a stable slug. (GLM exposes featureBullets only too — same.)
- Landing: client-side fetch with graceful fallback (hide section on error); ComingSoon greyed + no CTA;
  bilingual keys. (Same.)
- Isolation table + reasoning (Plan filter-free/authz-guarded like AppSetting; Subscription strict-own;
  enforcement counts via IgnoreQueryFilters + explicit OwnerId). (Same.)

## Minor decisions (accept GLM's defaults)
- Comments/month = **calendar month UTC** (both agree; simplest).
- `PriceMonthly` = `decimal` + `Currency` code; minor-units decision deferred (display-only until a gateway).
- Plan delete: soft-delete; block deleting Free (the fallback) and any plan with active subscriptions
  (Conflict + count, like role-delete-reassign). (Both agree.)
- Demo comment cap and plan `MaxCommentsPerMonth` coexist (tighter wins for demo tenants). (Both agree.)

## Net
The two plans merge cleanly: GLM's structure + the 6 "adopt" items + GLM's entitlement-storage, plus the
shared decisions. No blocking disagreement remains. → write MERGED-PLAN.md, then one GLM alignment pass.
