namespace OrderIngestion.Domain.Orders;

/// <summary>
/// An order as this system understands it. Independent of any upstream JSON shape:
/// sources map their own DTOs into this type.
/// </summary>
/// <param name="ExternalId">The order's ID in the source system.</param>
/// <param name="Payload">The record exactly as the source sent it (JSON). Stored as-is.</param>
public sealed record Order(
    string ExternalId,
    string CustomerId,
    OrderStatus Status,
    string Currency,
    decimal TotalAmount,
    DateTimeOffset CreatedAt,
    IReadOnlyList<OrderLine> Lines,
    string Payload)
{
    /// <summary>De-duplication key. Two orders with the same hash are the same order.</summary>
    public string CanonicalHash => OrderIdentity.CanonicalHash(ExternalId, CustomerId);
}

public sealed record OrderLine(string Sku, int Quantity, decimal UnitPrice);
