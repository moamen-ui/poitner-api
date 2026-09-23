namespace Pointer.API.Auth;

/// <summary>
/// DB-12 §3.8 — marks a controller action as security-relevant: it must write exactly one audit
/// row through <see cref="Pointer.Application.Abstractions.IAuditWriter"/>. <c>AuditCoverageFilter</c>
/// watches every <c>[Audited]</c> action and logs <c>AUDIT GAP</c> (and 500s under
/// <c>Audit:StrictCoverage=true</c>) when the action completed without a row. The action string must be an
/// <c>AuditActions</c> constant — <c>Tests/AuditCoverageTests</c> fails the build otherwise.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class AuditedAttribute(string action) : Attribute
{
    /// <summary>An <c>AuditActions.*</c> constant (never a literal at a call site).</summary>
    public string Action { get; } = action;
}

/// <summary>
/// DB-12 §3.8 — marks a mutating action as deliberately NOT audited; <paramref name="reason"/> is
/// required and non-empty (the coverage test enforces it) so an exemption is a written-down
/// decision, not an oversight.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class NoAuditAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}
