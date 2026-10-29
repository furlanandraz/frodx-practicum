using System.Globalization;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace OrderIngestion.Worker.Logging;

/// <summary>
/// Serilog configured in code rather than in appsettings.json, so every choice is visible here.
/// Reads two settings: Log:Level (trace|debug|info|warn|error|fatal) and Log:Directory.
/// </summary>
internal static class LoggingSetup
{
    // {level} is our short label (info/warn/error) from ShortLevelEnricher, not Serilog's {Level}.
    // ",-5" pads it so messages line up; ":l" prints it without quotes.
    private const string ConsoleTemplate = "[{Timestamp:HH:mm:ss} {level,-5:l}] {Message:lj}{NewLine}{Exception}";

    /// <summary>Console-only logger for errors that happen before configuration is loaded.</summary>
    public static Serilog.ILogger CreateBootstrapLogger() =>
        new LoggerConfiguration()
            .Enrich.With<ShortLevelEnricher>()
            .WriteTo.Console(outputTemplate: ConsoleTemplate, formatProvider: CultureInfo.InvariantCulture)
            .CreateBootstrapLogger();

    public static LoggerConfiguration Configure(this LoggerConfiguration logger, IConfiguration configuration)
    {
        var level = LogLevels.Parse(configuration["Log:Level"]);
        var directory = configuration["Log:Directory"]
            ?? throw new InvalidOperationException("Log:Directory is not set (see appsettings.json).");

        return logger
            .MinimumLevel.Is(level)
            // Framework noise (one line per HTTP request, HttpClient chatter) only from warn up;
            // startup and shutdown lines stay visible.
            .MinimumLevel.Override("Microsoft", Max(level, LogEventLevel.Warning))
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Max(level, LogEventLevel.Information))
            .MinimumLevel.Override("System.Net.Http.HttpClient", Max(level, LogEventLevel.Warning))
            .Enrich.FromLogContext()
            .Enrich.With<ShortLevelEnricher>()
            .Enrich.WithProperty("Application", "OrderIngestion.Worker")
            // Human-readable, for `docker compose logs`.
            .WriteTo.Console(outputTemplate: ConsoleTemplate, formatProvider: CultureInfo.InvariantCulture)
            // One JSON object per line, for searching (e.g. by RunId). A new file each day, two weeks kept.
            .WriteTo.File(
                new RenderedCompactJsonFormatter(),
                Path.Combine(directory, "worker-.json"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14);
    }

    private static LogEventLevel Max(LogEventLevel a, LogEventLevel b) => a > b ? a : b;
}

/// <summary>Maps between Serilog's level names and the short ones used in config and output.</summary>
internal static class LogLevels
{
    public static string ToShortName(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "trace",
        LogEventLevel.Debug => "debug",
        LogEventLevel.Information => "info",
        LogEventLevel.Warning => "warn",
        LogEventLevel.Error => "error",
        LogEventLevel.Fatal => "fatal",
        _ => level.ToString().ToLowerInvariant(),
    };

    public static LogEventLevel Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" => throw new InvalidOperationException("Log:Level is not set (see appsettings.json)."),
        "info" or "information" => LogEventLevel.Information,
        "trace" or "verbose" => LogEventLevel.Verbose,
        "debug" => LogEventLevel.Debug,
        "warn" or "warning" => LogEventLevel.Warning,
        "error" => LogEventLevel.Error,
        "fatal" => LogEventLevel.Fatal,
        _ => throw new InvalidOperationException(
            $"Unknown log level '{value}'. Use trace, debug, info, warn, error or fatal."),
    };
}

/// <summary>Adds a "level" property with the short name, used by the console template and visible in the JSON file.</summary>
internal sealed class ShortLevelEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) =>
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("level", LogLevels.ToShortName(logEvent.Level)));
}
