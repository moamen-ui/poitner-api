namespace Pointer.Application.Common;

/// <summary>
/// The whitelist for audit <c>before</c>/<c>after</c> keys (DB-12 §3.5, R12). audit_events cannot
/// be scrubbed (append-only), so the only way R14's erase inventory can say "kept by design" is
/// that these keys never carry a person's name, address, a secret or content. Adding a key is a PR
/// that names the reviewer; adding a personal field is refused. Keys are added, never repurposed.
/// </summary>
public static class AuditFields
{
    private const int MaxValueLength = 200;

    public static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "role_id",
        "role_name",
        "is_active",
        "approval_status",
        "plan_id",
        "plan_name",
        "status",
        "action",
        "name",
        "key",
        "url",
        "environment_id",
        "reason",
        "minutes",
        "count",
        "keys",
        "scopes",
        "label",
        "kind",
        "max_uses",
        "expires_at",
        "email_hash",
        "session_id",
        "request_count",
        "duration_seconds",
        "source",
        "project_id",
        "with_password",
    };

    /// <summary>
    /// Drops keys not in <see cref="Allowed"/> and truncates values to 200 chars. Never in the
    /// whitelist: email, display_name, password, hash, token, code, body, prompt, text — names
    /// and addresses of people, secrets, content.
    /// </summary>
    public static Dictionary<string, string> Sanitize(IReadOnlyDictionary<string, string>? fields)
    {
        if (fields is null || fields.Count == 0)
            return new Dictionary<string, string>();

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in fields)
        {
            if (!Allowed.Contains(key))
                continue;
            result[key] = value.Length <= MaxValueLength ? value : value[..MaxValueLength];
        }
        return result;
    }
}
