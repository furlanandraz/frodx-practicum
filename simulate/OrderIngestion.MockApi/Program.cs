using System.Text.Json.Nodes;
using OrderIngestion.MockApi;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHealthChecks();

var options = builder.Configuration.GetSection(MockOptions.SectionName).Get<MockOptions>()
    ?? throw new InvalidOperationException("The Mock section is missing from configuration (see appsettings.json).");
var responsesPath = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, options.ResponsesPath));

var initial = options.ToSettings();
if (initial.Validate(responsesPath) is { } problem)
{
    throw new InvalidOperationException($"Invalid Mock settings: {problem}");
}

var state = new MockState(initial);

var app = builder.Build();

app.Logger.LogInformation("Serving scenarios from {ResponsesPath} with {Settings}", responsesPath, initial);

app.MapGet("/orders", async (string? scenario, DateTimeOffset? since, ILogger<Program> logger, CancellationToken cancellationToken) =>
{
    var settings = state.Current;
    logger.LogInformation("GET /orders (scenario {Scenario}, since {Since})", scenario ?? settings.Scenario, since?.ToString("O") ?? "-");

    if (settings.LatencyMs > 0)
    {
        await Task.Delay(settings.LatencyMs, cancellationToken);
    }

    if (Random.Shared.NextDouble() < settings.FailureRate)
    {
        logger.LogWarning("Simulating upstream failure ({StatusCode})", settings.FailureStatusCode);
        return Results.Problem("Simulated upstream failure", statusCode: settings.FailureStatusCode);
    }

    var name = scenario ?? settings.Scenario;
    if (!MockSettings.IsValidScenarioName(name))
    {
        return Results.BadRequest("Scenario names may only contain a-z, 0-9 and '-'.");
    }

    var file = Path.Combine(responsesPath, $"{name}.json");
    if (!File.Exists(file))
    {
        return Results.NotFound($"Unknown scenario '{name}'.");
    }

    var json = await File.ReadAllTextAsync(file, cancellationToken);
    return Results.Text(since is null ? json : FilterSince(json, since.Value), "application/json");
});

app.MapGet("/_mock/settings", () => state.Current);

app.MapPut("/_mock/settings", (MockSettingsPatch patch, ILogger<Program> logger) =>
{
    var next = state.Current.With(patch);
    if (next.Validate(responsesPath) is { } invalid)
    {
        return Results.BadRequest(invalid);
    }

    state.Current = next;
    logger.LogInformation("Settings changed to {Settings}", next);
    return Results.Ok(next);
});

app.MapDelete("/_mock/settings", (ILogger<Program> logger) =>
{
    state.Current = state.Initial;
    logger.LogInformation("Settings reset to {Settings}", state.Initial);
    return Results.Ok(state.Initial);
});

app.MapHealthChecks("/health");

app.Run();

static string FilterSince(string json, DateTimeOffset since)
{
    if (JsonNode.Parse(json) is not JsonArray orders)
    {
        return json;
    }

    var kept = new JsonArray();
    foreach (var order in orders)
    {
        var createdAt = order?["createdAt"] is JsonValue value && value.TryGetValue<string>(out var text)
            && DateTimeOffset.TryParse(text, out var parsed)
                ? parsed
                : (DateTimeOffset?)null;

        if (createdAt is null || createdAt >= since)
        {
            kept.Add(order?.DeepClone());
        }
    }

    return kept.ToJsonString();
}
