namespace OrderIngestion.MockApi;

/// <summary>Bound from the "Mock" config section (appsettings.json, overridable by env: Mock__Scenario, ...).</summary>
internal sealed class MockOptions
{
    public const string SectionName = "Mock";

    /// <summary>Folder with scenario files, relative to the content root.</summary>
    public string ResponsesPath { get; init; } = string.Empty;

    public string Scenario { get; init; } = string.Empty;

    public double FailureRate { get; init; }

    public int FailureStatusCode { get; init; }

    public int LatencyMs { get; init; }

    public MockSettings ToSettings() => new(Scenario, FailureRate, FailureStatusCode, LatencyMs);
}

/// <summary>
/// What the mock does right now. Starts from config and can be changed at runtime through
/// PUT /_mock/settings, so tests can switch behaviour without restarting the container.
/// </summary>
/// <param name="Scenario">File in the responses folder, without ".json", served by GET /orders.</param>
/// <param name="FailureRate">Share of requests that fail, from 0.0 (never) to 1.0 (always).</param>
/// <param name="FailureStatusCode">Status of a simulated failure: 503 exercises retries, 404 the not-found alert.</param>
/// <param name="LatencyMs">Delay added to every /orders response. Above the worker's attempt timeout, it forces retries.</param>
internal sealed record MockSettings(string Scenario, double FailureRate, int FailureStatusCode, int LatencyMs)
{
    public MockSettings With(MockSettingsPatch patch) => new(
        patch.Scenario ?? Scenario,
        patch.FailureRate ?? FailureRate,
        patch.FailureStatusCode ?? FailureStatusCode,
        patch.LatencyMs ?? LatencyMs);

    /// <returns>Null when valid, otherwise what is wrong.</returns>
    public string? Validate(string responsesPath) =>
        !IsValidScenarioName(Scenario) ? "scenario may only contain a-z, 0-9 and '-'"
        : !File.Exists(Path.Combine(responsesPath, $"{Scenario}.json")) ? $"there is no scenario file {Scenario}.json"
        : FailureRate is < 0 or > 1 ? "failureRate must be between 0 and 1"
        : FailureStatusCode is < 400 or > 599 ? "failureStatusCode must be a 4xx or 5xx code"
        : LatencyMs is < 0 or > 600_000 ? "latencyMs must be between 0 and 600000"
        : null;

    // Only letters, digits and '-', so a scenario name can never point outside the responses folder.
    public static bool IsValidScenarioName(string name) =>
        name.Length > 0 && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
}

/// <summary>Body of PUT /_mock/settings. Fields left out keep their current value.</summary>
internal sealed record MockSettingsPatch(string? Scenario, double? FailureRate, int? FailureStatusCode, int? LatencyMs);

/// <summary>Holds the current settings. Replaced as a whole, so a request never sees a half-applied change.</summary>
internal sealed class MockState(MockSettings initial)
{
    private volatile MockSettings _current = initial;

    public MockSettings Initial { get; } = initial;

    public MockSettings Current
    {
        get => _current;
        set => _current = value;
    }
}
