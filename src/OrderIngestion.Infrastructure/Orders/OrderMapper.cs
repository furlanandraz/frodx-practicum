using OrderIngestion.Domain.Orders;

namespace OrderIngestion.Infrastructure.Orders;

/// <summary>A record that parsed as JSON but breaks a rule (unknown status, empty lines, ...).</summary>
internal sealed class InvalidOrderException(string message) : Exception(message);

/// <summary>
/// Translates the upstream DTO into the domain model and checks the rules the JSON shape
/// alone can't express. This is the boundary where vendor data becomes trusted data.
/// </summary>
internal static class OrderMapper
{
    /// <param name="payload">The record's raw JSON, kept on the order so it can be stored as received.</param>
    public static Order ToDomain(this OrderDto dto, string payload)
    {
        Require(!string.IsNullOrWhiteSpace(dto.Id), "id is empty");
        Require(!string.IsNullOrWhiteSpace(dto.CustomerId), "customerId is empty");
        Require(TryParseStatus(dto.Status, out var status), $"unknown status '{dto.Status}'");
        Require(dto.Currency.Length == 3, $"currency '{dto.Currency}' is not a 3-letter code");
        Require(dto.TotalAmount >= 0, $"totalAmount {dto.TotalAmount} is negative");
        Require(dto.Lines.Count > 0, "order has no lines");

        return new Order(
            ExternalId: dto.Id,
            CustomerId: dto.CustomerId,
            Status: status,
            Currency: dto.Currency.ToUpperInvariant(),
            TotalAmount: dto.TotalAmount,
            CreatedAt: dto.CreatedAt.ToUniversalTime(),
            Lines: dto.Lines.Select((line, index) => ToDomain(line, number: index + 1)).ToList(),
            Payload: payload);
    }

    private static OrderLine ToDomain(OrderLineDto line, int number)
    {
        Require(!string.IsNullOrWhiteSpace(line.Sku), $"line {number}: sku is empty");
        Require(line.Quantity > 0, $"line {number} ({line.Sku}): quantity must be positive");
        Require(line.UnitPrice >= 0, $"line {number} ({line.Sku}): unitPrice is negative");

        return new OrderLine(line.Sku, line.Quantity, line.UnitPrice);
    }

    // Matches enum names only. Enum.TryParse would also accept numbers like "3".
    private static bool TryParseStatus(string value, out OrderStatus status)
    {
        foreach (var candidate in Enum.GetValues<OrderStatus>())
        {
            if (string.Equals(candidate.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                status = candidate;
                return true;
            }
        }

        status = default;
        return false;
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition)
        {
            throw new InvalidOrderException(reason);
        }
    }
}
