using System;
using System.Threading.Tasks;
using ExpenseHub.Core.Data;
using ExpenseHub.Core.Reports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ExpenseHub.Worker;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddExpenseHubDatabase(builder.Configuration);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection("RabbitMq"));
        builder.Services.Configure<ReportProcessingOptions>(builder.Configuration.GetSection("ReportProcessing"));
        builder.Services.AddScoped<IReportProcessingStore, EfReportProcessingStore>();
        builder.Services.AddScoped<ReportProcessor>();
        builder.Services.AddHostedService<ReportConsumerService>();

        using IHost host = builder.Build();
        await host.RunAsync();
    }
}
