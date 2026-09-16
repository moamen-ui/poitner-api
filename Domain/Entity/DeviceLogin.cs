namespace Pointer.Domain.Entity;

/// <summary>Lifecycle of a device-code sign-in row. There is no "Expired" member on purpose —
/// expiry is a function of <see cref="DeviceLogin.ExpiresAt"/> vs now, computed at read time
/// rather than written, so nothing has to sweep rows the instant they expire.</summary>
public enum DeviceLoginStatus
{
    Pending = 0,
    Approved = 1,
    Denied = 2,

    /// <summary>The approved row's API key has already been handed to the CLI once. A later poll
    /// on a Consumed row reports "expired" — the same terminal outcome as a row nobody ever
    /// approved, so a leaked device code cannot be replayed to fetch the key again.</summary>
    Consumed = 3,
}

/// <summary>
/// Browser-based ("device code") sign-in for the CLI — the same shape as `gh auth login`. The CLI
/// calls POST /api/auth/device/start to mint a (deviceCode, userCode) pair, shows the user code and
/// opens the dashboard's /cli-login page, then polls POST /api/auth/device/poll with the device
/// code until the developer approves (or denies) it there. On the first poll after approval the raw
/// personal API key (<see cref="Application.Services.Interfaces.IApiKeyService.GetOrCreateAsync"/>
/// — the same one the profile page shows) is handed back exactly once and the row is marked
/// Consumed.
/// </summary>
/// <remarks>
/// <see cref="DeviceCodeHash"/> is the SHA-256 of the raw device code — the raw value is never
/// stored, only ever handed to the CLI once at Start, mirroring
/// <see cref="Common.QuickAccessTokenGenerator"/>'s magic-link tokens.
///
/// Exempt from the tenant query filter (see AppDbContext.OnModelCreating), like
/// <see cref="QuickAccessLink"/>: /device/start and /device/poll are anonymous by necessity (the
/// caller has no session, let alone a tenant claim, until approved) and always read/write this
/// table via <c>IgnoreQueryFilters()</c> explicitly. <see cref="OwnerId"/> is still stamped from
/// the approving user for parity with every other tenant-scoped table (and in case a future admin
/// listing wants to scope by tenant) even though nothing today queries this table through the
/// normal filtered path.
/// </remarks>
public class DeviceLogin : BaseEntity
{
    public string DeviceCodeHash { get; set; } = string.Empty;

    /// <summary>The short code the developer types into the dashboard, e.g. "ABCD-EFGH". Unique
    /// only among non-terminal (Pending/Approved-not-yet-consumed) rows — enforced in
    /// DeviceLoginService, not the database, since a code may legitimately repeat once its row is
    /// Denied/Consumed/expired.</summary>
    public string UserCode { get; set; } = string.Empty;

    /// <summary>Human label for the approval screen, e.g. "pointer-feedback CLI on Moamen's MacBook".</summary>
    public string ClientName { get; set; } = string.Empty;

    public DeviceLoginStatus Status { get; set; } = DeviceLoginStatus.Pending;

    /// <summary>The user who approved/denied this row — set only once Status leaves Pending.</summary>
    public Guid? UserId { get; set; }

    /// <summary>Tenant of the approving user. Query-filter parity only; see class remarks —
    /// this table is exempt from the tenant filter.</summary>
    public Guid? OwnerId { get; set; }

    public DateTime ExpiresAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
}
