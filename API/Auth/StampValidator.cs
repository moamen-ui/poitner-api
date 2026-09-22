using System;

namespace Pointer.API.Auth;

/// <summary>
/// Cached security-stamp state for an identity and optional workspace membership (DB-RULES R16).
/// </summary>
public record StampValidationState(Guid? UserStamp, Guid? MembershipStamp, bool MembershipLive);

/// <summary>
/// DB-11a / DB-RULES R16: session is (identity, workspace). On token validation, checks the identity stamp
/// and, when a tenant claim is present, validates the membership stamp and active/approved state.
/// Pure decision method (Validate) is unit-tested; DB loading and caching live in AuthenticationExtensions.
/// </summary>
public static class StampValidator
{
    /// <summary>
    /// Pure decision: returns true if the token's stamps and workspace membership match live DB state.
    /// Fails when identity stamp mismatches, or when a tenant claim is present and the membership
    /// is missing, inactive, not approved, or its security stamp differs from mstamp (DB-RULES R16).
    /// Super-admin tokens (hasTenant = false) skip the membership check.
    /// </summary>
    public static bool Validate(
        Guid tokenStamp,
        Guid? tokenMstamp,
        bool hasTenant,
        StampValidationState? state
    )
    {
        if (state?.UserStamp is null || state.UserStamp.Value != tokenStamp)
            return false;

        if (hasTenant)
        {
            if (tokenMstamp is null)
                return false;

            if (state.MembershipStamp is null || !state.MembershipLive)
                return false;

            if (state.MembershipStamp.Value != tokenMstamp.Value)
                return false;
        }

        return true;
    }

    /// <summary>Convenience overload taking individual stamp/live values.</summary>
    public static bool Validate(
        Guid tokenStamp,
        Guid? tokenMstamp,
        bool hasTenant,
        Guid? currentUserStamp,
        Guid? currentMembershipStamp,
        bool isMembershipLive
    ) =>
        Validate(
            tokenStamp,
            tokenMstamp,
            hasTenant,
            new StampValidationState(currentUserStamp, currentMembershipStamp, isMembershipLive)
        );
}
