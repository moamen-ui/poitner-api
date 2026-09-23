using Pointer.Application.Abstractions;

namespace Pointer.Application.Common;

/// <summary>
/// DB-12: a do-nothing <see cref="IAuditWriter"/> used ONLY as the constructor default for services
/// that also support hand-rolled (non-DI) construction — a great many unit tests build a service
/// directly with `new` and supply only the collaborators the test actually cares about. Real request
/// traffic never reaches this: <c>IAuditWriter</c> is registered in DI
/// (<c>Infrastructure/DependencyInjection.cs</c>), so ASP.NET Core's container always resolves and
/// injects the real <c>AuditWriter</c> for every constructor parameter, default value or not. A test
/// that DOES want to assert on emitted audit rows passes <c>Tests.FakeAuditWriter</c> explicitly,
/// which this default is never allowed to shadow (it is only ever used when nothing at all was
/// passed).
/// </summary>
public sealed class NoopAuditWriter : IAuditWriter
{
    public static readonly NoopAuditWriter Instance = new();

    public Task WriteAsync(AuditEntry entry, CancellationToken ct = default) => Task.CompletedTask;
}
