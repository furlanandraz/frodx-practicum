namespace OrderIngestion.Infrastructure.Orders;

/// <summary>
/// Our predefined schema for an order in the upstream API's JSON. Lives in Infrastructure, not
/// Domain, because it has to change whenever the vendor changes their payload. Every field is
/// required and no other fields are accepted (strict mode in <see cref="OrderPayloadReader"/>).
/// </summary>
internal sealed record OrderDto(
    string Id,
    string CustomerId,
    string Status,
    string Currency,
    decimal TotalAmount,
    DateTimeOffset CreatedAt,
    IReadOnlyList<OrderLineDto> Lines);

internal sealed record OrderLineDto(string Sku, int Quantity, decimal UnitPrice);
