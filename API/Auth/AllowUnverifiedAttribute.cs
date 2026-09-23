namespace Pointer.API.Auth;

/// <summary>
/// DB-14 §3.4. Marks an admin-surface action (one that would otherwise be gated by
/// <see cref="RequireVerifiedEmailFilter"/>) as reachable by an unverified identity. None is needed
/// today — the attribute exists so a future exception is explicit and greppable rather than the
/// filter growing an ad-hoc exemption list.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class AllowUnverifiedAttribute : Attribute { }
