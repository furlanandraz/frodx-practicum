using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OrderIngestion.Domain.Alerts;
using OrderIngestion.Domain.Ingestion;
using OrderIngestion.Infrastructure;
using OrderIngestion.Infrastructure.Alerts;
using OrderIngestion.Worker.Alerts;
using OrderIngestion.Worker.Ingestion;
using OrderIngestion.Worker.Logging;
using Serilog;

Log.Logger = LoggingSetup.CreateBootstrapLogger();

try
{
    // A plain .NET host: configuration, dependency injection, logging and clean shutdown.
    // No web server: the worker exposes nothing.
    var builder = Host.CreateApplicationBuilder(args);

    // Serilog replaces the default providers. writeToProviders lets providers added
    // below (Azure Monitor, when enabled) still receive every log event.
    builder.Logging.ClearProviders();
    builder.Services.AddSerilog(
        (services, logger) => logger.ReadFrom.Services(services).Configure(builder.Configuration),
        writeToProviders: true);

    builder.Services.AddOptions<IngestionOptions>()
        .Bind(builder.Configuration.GetSection(IngestionOptions.SectionName))
        .ValidateDataAnnotations()
        .ValidateOnStart();

    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddInfrastructure(builder.Configuration);

    // Ingestion
    builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<IngestionOptions>>().Value.ToSettings());
    builder.Services.AddScoped<OrderIngestionService>();
    builder.Services.AddHostedService<IngestionWorker>();

    // Alerts
    builder.Services.AddSingleton(sp => new AlertThrottle(
        sp.GetRequiredService<TimeProvider>(),
        TimeSpan.FromMinutes(sp.GetRequiredService<IOptions<AlertOptions>>().Value.ThrottleMinutes)));
    builder.Services.AddSingleton<AlertDispatcher>();

    // Azure Application Insights (scaffold): off unless a connection string is configured.
    // When on, every log line, including each cycle's start and result, also goes there.
    var appInsightsConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
    if (!string.IsNullOrWhiteSpace(appInsightsConnectionString))
    {
        builder.Logging.AddOpenTelemetry(logging =>
            logging.AddAzureMonitorLogExporter(exporter => exporter.ConnectionString = appInsightsConnectionString));
    }

    var host = builder.Build();

    await host.Services.InitializeDatabaseAsync();

    await host.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Worker terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

return 0;
