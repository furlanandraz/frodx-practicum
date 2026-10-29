using System.ComponentModel.DataAnnotations;

namespace OrderIngestion.Infrastructure.Orders;

/// <summary>
/// Bound from the "OrderApi" config section. Values come from appsettings.json (the only place
/// defaults live) and can be overridden by environment variables. Checked at startup.
/// </summary>
public sealed class OrderApiOptions
{
    public const string SectionName = "OrderApi";

    [Required, Url]
    public string BaseUrl { get; init; } = string.Empty;

    [Required]
    public string OrdersPath { get; init; } = string.Empty;

    /// <summary>The request is abandoned after this long; the next cycle tries again.</summary>
    [Range(1, 300)]
    public int TimeoutSeconds { get; init; }
}
