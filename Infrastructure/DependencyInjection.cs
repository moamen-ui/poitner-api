using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
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
        // Build the connection string via NpgsqlConnectionStringBuilder so we can guarantee a sane
        // MaxPoolSize cap (default otherwise is Npgsql's 100). Honor any value already supplied in the
        // configured connection string — check all accepted spellings by stripping whitespace, so
        // "MaxPoolSize", "Max Pool Size" and Npgsql's canonical "Maximum Pool Size" are all respected.
        var raw = c.GetConnectionString("Default") ?? string.Empty;
        var csb = new NpgsqlConnectionStringBuilder(raw);
        var norm = new string(raw.Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToUpperInvariant();
        if (!norm.Contains("MAXPOOLSIZE") && !norm.Contains("MAXIMUMPOOLSIZE"))
            csb.MaxPoolSize = 40;

        s.AddDbContext<AppDbContext>(o => o.UseNpgsql(csb.ConnectionString,
            n => n.EnableRetryOnFailure(5, TimeSpan.FromSeconds(30), null)));
        // In-process cache for hot near-static reads (settings; also used by the auth security-stamp
        // check). Idempotent with any other AddMemoryCache registration.
        s.AddMemoryCache();
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
