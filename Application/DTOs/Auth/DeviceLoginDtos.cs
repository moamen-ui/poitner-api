namespace Pointer.Application.DTOs.Auth;

/// <summary>Body for POST /api/auth/device/start.</summary>
public class DeviceLoginStartRequest
{
    /// <summary>Human label for the approval screen, e.g. "pointer-feedback CLI on Moamen's MacBook".
    /// Falls back to a generic label when blank.</summary>
    public string? ClientName { get; set; }
}

public class DeviceLoginStartResponse
{
    /// <summary>43-char url-safe secret the CLI polls with. Never shown to the user.</summary>
    public string DeviceCode { get; set; } = string.Empty;

    /// <summary>Short human code, e.g. "ABCD-EFGH", typed into the dashboard.</summary>
    public string UserCode { get; set; } = string.Empty;

    /// <summary>Branding Urls.App + "/cli-login?code=" + UserCode — where the CLI opens the browser.</summary>
    public string VerificationUrl { get; set; } = string.Empty;

    public int ExpiresInSeconds { get; set; }
    public int IntervalSeconds { get; set; }
}

/// <summary>Body for POST /api/auth/device/poll.</summary>
public class DeviceLoginPollRequest
{
    public string DeviceCode { get; set; } = string.Empty;
}

public class DeviceLoginPollResponse
{
    /// <summary>"pending" | "approved" | "denied" | "expired" | "unknown".</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Present exactly once — the first poll after approval. Never repeated.</summary>
    public string? ApiKey { get; set; }
    public string? DisplayName { get; set; }
    public string? Email { get; set; }

    /// <summary>The server origin the key was minted on — lets the CLI confirm it's saving the key
    /// against the same server it started the flow with.</summary>
    public string? Server { get; set; }
}

/// <summary>Body for POST /api/auth/device/approve and /deny.</summary>
public class DeviceLoginUserCodeRequest
{
    public string UserCode { get; set; } = string.Empty;
}

/// <summary>Returned by GET /api/auth/device/{userCode}, /approve and /deny.</summary>
public class DeviceLoginInfoResponse
{
    public string UserCode { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    /// <summary>"pending" | "approved" | "denied" | "expired".</summary>
    public string Status { get; set; } = string.Empty;
}
