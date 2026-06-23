using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
namespace Pointer.Application;
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection s)
    {
        var asm = typeof(IApplicationAssemblyReference).Assembly;
        s.AddValidatorsFromAssembly(asm);
        s.Scan(scan => scan.FromAssemblies(asm)
            .AddClasses(c => c.Where(t => t.Name.EndsWith("Service")))
            .AsImplementedInterfaces().WithScopedLifetime());
        return s;
    }
}
