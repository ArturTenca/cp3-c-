using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ExpenseHub.Core.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ExpenseHub.Core.Data;

/// <summary>Registro do banco e inicialização idempotente (schema, roles e Admin).</summary>
internal static class DatabaseSetup
{
    public static IServiceCollection AddExpenseHubDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        string connectionString = configuration.GetConnectionString("ExpenseHub")
            ?? throw new InvalidOperationException("ConnectionStrings:ExpenseHub não configurada.");
        services.AddDbContext<ExpenseHubDbContext>(options => options.UseSqlite(connectionString));
        return services;
    }

    public static async Task InitializeAsync(IServiceProvider services, IConfiguration configuration, CancellationToken cancellationToken)
    {
        using IServiceScope scope = services.CreateScope();
        ExpenseHubDbContext db = scope.ServiceProvider.GetRequiredService<ExpenseHubDbContext>();
        ILogger logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseSetup");

        await db.Database.EnsureCreatedAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);

        RoleManager<IdentityRole> roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (string role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new IdentityRole(role));
            }
        }

        string? email = configuration["Seed:AdminEmail"];
        string? password = configuration["Seed:AdminPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("Seed:AdminEmail/Seed:AdminPassword ausentes; conta Admin não criada");
            return;
        }

        UserManager<IdentityUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        IdentityUser? admin = await userManager.FindByEmailAsync(email);
        if (admin is null)
        {
            admin = new IdentityUser { UserName = email, Email = email, EmailConfirmed = true };
            IdentityResult created = await userManager.CreateAsync(admin, password);
            if (!created.Succeeded)
            {
                throw new InvalidOperationException("Falha ao criar Admin: " + string.Join("; ", created.Errors.Select(e => e.Code)));
            }
        }

        if (!await userManager.IsInRoleAsync(admin, Roles.Admin))
        {
            await userManager.AddToRoleAsync(admin, Roles.Admin);
        }
    }
}
