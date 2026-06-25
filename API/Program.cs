using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Pointer.API.Extensions;
using Pointer.API.Seed;
using Pointer.Application;
using Pointer.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers(options =>
{
    options.Filters.Add(new Microsoft.AspNetCore.Mvc.ProducesAttribute("application/json"));
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddJwtAuth(builder.Configuration);
builder.Services.AddAuthorization();

builder.Services.AddCors(o =>
    o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod())
);

builder.Services.AddSwaggerGen(c =>
{
    c.AddSecurityDefinition(
        "Bearer",
        new OpenApiSecurityScheme
        {
            In = ParameterLocation.Header,
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
        }
    );

    c.AddSecurityRequirement(
        new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "Bearer",
                    },
                },
                Array.Empty<string>()
            },
        }
    );
});

var app = builder.Build();

if (builder.Configuration.GetValue<bool>("DBMigrationEnabled"))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await AdminSeeder.SeedAsync(app.Services);
}

app.UseSwagger();
app.UseSwaggerUI();

// Serve the skill markdown with the request origin injected into the
// <POINTER_SERVER> placeholder, so any AI tool that fetches a skill from the
// deployed URL gets it pre-filled with that URL (no localhost, nothing to ask).
// Runs before UseStaticFiles so it takes precedence over the raw file.
var skillFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/pointer-init.md", "/skill.md" };
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? string.Empty;
    if (HttpMethods.IsGet(ctx.Request.Method) && skillFiles.Contains(path))
    {
        var webRoot = app.Environment.WebRootPath
            ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
        var file = Path.Combine(webRoot, path.TrimStart('/'));
        if (File.Exists(file))
        {
            var origin = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            var text = (await File.ReadAllTextAsync(file)).Replace("<POINTER_SERVER>", origin);
            ctx.Response.ContentType = "text/markdown; charset=utf-8";
            await ctx.Response.WriteAsync(text);
            return;
        }
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
