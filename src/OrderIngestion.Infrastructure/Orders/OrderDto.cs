namespace OrderIngestion.Infrastructure.Orders;

/// <summary>
/// Our predefined schema for an order in the upstream API's JSON. Lives in Infrastructure, not
/// Domain, because it has to change whenever the vendor changes their payload.
/// </summary>
/// <remarks>
/// Every field is required and none may be null. Nulls are caught before parsing and skip that one
/// order (see <see cref="OrderPayloadReader"/>); everything else that doesn't fit this shape exactly
/// (a field missing, added or of another JSON type) is schema drift.
/// </remarks>
internal sealed record OrderDto(
    string Id,
    string CustomerId,
    string Status,
    string Currency,
    decimal TotalAmount,
    DateTimeOffset CreatedAt,
    IReadOnlyList<OrderLineDto> Lines);

internal sealed record OrderLineDto(string Sku, int Quantity, decimal UnitPrice);
