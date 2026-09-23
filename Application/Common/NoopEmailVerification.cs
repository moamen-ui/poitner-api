using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;

namespace Pointer.Application.Common;

/// <summary>
/// DB-14: a do-nothing <see cref="IEmailVerificationService"/> used ONLY as the constructor default
/// for services that also support hand-rolled (non-DI) construction — same rationale as
/// <see cref="NoopAuditWriter"/>. Real request traffic never reaches this: <c>IEmailVerificationService</c>
/// is auto-registered in DI (Scrutor scan, <c>Application/DependencyInjection.cs</c>), so ASP.NET
/// Core's container always resolves and injects the real <c>EmailVerificationService</c> for every
/// constructor parameter, default value or not. A test that wants to assert a verification mail was
/// sent constructs the real service (with a <c>CapturingEmail</c> double) and passes it explicitly.
/// <para>
/// Deliberately NOT named <c>*Service</c> — the Scrutor scan in <c>Application/DependencyInjection.cs</c>
/// registers every class whose name ends in "Service" against its implemented interfaces, and this
/// type must never compete with the real <c>EmailVerificationService</c> for that registration.
/// </para>
/// </summary>
public sealed class NoopEmailVerification : IEmailVerificationService
{
    public static readonly NoopEmailVerification Instance = new();

    private NoopEmailVerification() { }

    public Task SendAsync(User identity) => Task.CompletedTask;

    public Task<Result> ResendAsync() =>
        Task.FromResult(Result.Failure("Email verification is not configured."));

    public Task<Result> ConfirmAsync(string token) =>
        Task.FromResult(Result.Failure("Email verification is not configured."));

    public void InvalidateGate(Guid publicId) { }
}
