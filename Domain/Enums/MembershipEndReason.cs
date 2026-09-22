namespace Pointer.Domain.Enums;

/// <summary>Append-only (DB-RULES R10 — enum ints, never renumber). Why a WorkspaceMembership ended.</summary>
public enum MembershipEndReason
{
    Removed = 1,
    Left = 2,
    AccountErased = 3,
}
