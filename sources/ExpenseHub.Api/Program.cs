using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ExpenseHub.Api.Endpoints;
using ExpenseHub.Api.Infrastructure;
using ExpenseHub.Core.Data;
using ExpenseHub.Core.Expenses;
using ExpenseHub.Core.Reports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ExpenseHub.Api;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        builder.Services.AddOpenApi();
        builder.Services.AddProblemDetails();
        builder.Services.ConfigureHttpJsonOptions(options => ReportJson.Configure(options.SerializerOptions));

        builder.Services.AddExpenseHubDatabase(builder.Configuration);
        builder.Services.AddDataProtection()
            .SetApplicationName("ExpenseHub")
            .PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["DataProtection:KeysPath"] ?? "data-protection-keys"));

        builder.Services.AddAuthentication(IdentityConstants.BearerScheme).AddBearerToken(IdentityConstants.BearerScheme);
        builder.Services.AddAuthorization();
        builder.Services.AddIdentityCore<IdentityUser>(options => options.User.RequireUniqueEmail = true)
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<ExpenseHubDbContext>()
            .AddApiEndpoints();

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection("RabbitMq"));
        builder.Services.AddSingleton<IReportPublisher, RabbitMqReportPublisher>();
        builder.Services.AddScoped<IReportRequestRepository, EfReportRequestRepository>();
        builder.Services.AddScoped<ReportService>();
        builder.Services.AddScoped<ExpenseService>();

        WebApplication app = builder.Build();

        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseAuthentication();
        app.UseMiddleware<SecurityStampMiddleware>();
        app.UseAuthorization();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        string version = app.Configuration["APP_VERSION"] ?? "local";
        app.MapGet("/health", () => Results.Ok(new { status = "ok", version }))
            .WithName("GetHealth");
        app.MapIdentityApi<IdentityUser>();
        app.MapAdminEndpoints();
        app.MapExpenseEndpoints();
        app.MapReportEndpoints();

        await DatabaseSetup.InitializeAsync(app.Services, app.Configuration, CancellationToken.None);
        await app.RunAsync();
    }
}
