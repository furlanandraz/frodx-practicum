using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OrderIngestion.Domain.Alerts;
using OrderIngestion.Domain.Ingestion;
using OrderIngestion.Domain.Orders;
using OrderIngestion.Infrastructure.Alerts;
using OrderIngestion.Infrastructure.Orders;
using OrderIngestion.Infrastructure.Persistence;

namespace OrderIngestion.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Registers every Infrastructure adapter. Called once from the host's Program.cs.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OrderApiOptions>()
            .Bind(configuration.GetSection(OrderApiOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<AlertOptions>()
            .Bind(configuration.GetSection(AlertOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                o => o.IsCompleteWhenEnabled(),
                "With Alerts:Enabled, Alerts:SmtpHost must be set and Alerts:From / Alerts:To must be valid email addresses.")
            .ValidateOnStart();

        // Typed HttpClient: the factory manages connection pooling and DNS refresh.
        services.AddHttpClient<IOrderSource, HttpOrderSource>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<OrderApiOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });

        services.AddSingleton<SqliteDatabase>();
        services.AddScoped<IIngestionStore, SqliteIngestionStore>();
        services.AddSingleton<IAlertNotifier, SmtpAlertNotifier>();

        return services;
    }

    /// <summary>Creates the database file and tables if they don't exist yet. Call once before the worker starts.</summary>
    public static Task InitializeDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default) =>
        services.GetRequiredService<SqliteDatabase>().InitializeAsync(cancellationToken);
}
