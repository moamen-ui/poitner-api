using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pointer.Application.Abstractions;
using Pointer.Infrastructure.Auth;
using Pointer.Infrastructure.Billing;
using Pointer.Infrastructure.CurrentUser;
using Pointer.Infrastructure.Email;
using Pointer.Infrastructure.Repository;
using Pointer.Infrastructure.Storage;
namespace Pointer.Infrastructure;
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection s, IConfiguration c)
    {
        s.AddDbContext<AppDbContext>(o => o.UseNpgsql(c.GetConnectionString("Default"),
            n => n.EnableRetryOnFailure(5, TimeSpan.FromSeconds(30), null)));
        s.Configure<JwtOptions>(c.GetSection("JWT"));
        s.AddHttpContextAccessor();
        s.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        s.AddScoped<IUnitOfWork, UnitOfWork>();
        s.AddScoped<IPasswordHasher, BcryptPasswordHasher>();
        s.AddScoped<ITokenService, JwtTokenService>();
        s.AddScoped<ICurrentUser, HttpCurrentUser>();
        s.AddScoped<IFileStorage, LocalFileStorage>();
        s.AddSingleton<IUploadSigner, UploadSigner>();
        s.AddSingleton<IResetTokenService, ResetTokenService>();
        s.AddHttpClient<IEmailSender, BrevoEmailSender>();
        // Payment-ready seam — MANUAL DI (single-instance seam, not Scrutor). Swap for a real gateway
        // adapter here later via config; no schema churn.
        s.AddScoped<IBillingProvider, NoopBillingProvider>();
        return s;
    }
}
