using System.Text.Json;
using OrderIngestion.Domain.Orders;

namespace OrderIngestion.Infrastructure.Orders;

/// <summary>
/// Checks a response body against our schema and turns it into orders. All or nothing: the first
/// record that doesn't match makes the whole response schema drift, so nothing from it is stored.
/// </summary>
internal static class OrderPayloadReader
{
    // .NET's built-in strict mode: every field required, no unknown or duplicate fields, no nulls.
    // Names must match exactly. C# names properties in PascalCase (CustomerId), the API sends
    // camelCase (customerId); the camelCase policy tells the parser to compare them that way.
    private static readonly JsonSerializerOptions Strict = new(JsonSerializerOptions.Strict)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <exception cref="OrderSourceException">With <see cref="OrderSourceFailure.SchemaDrift"/>.</exception>
    public static IReadOnlyList<Order> Read(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw Drift($"Response is not valid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw Drift($"Expected a JSON array of orders, got {document.RootElement.ValueKind}");
            }

            var orders = new List<Order>();
            var index = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                try
                {
                    var dto = element.Deserialize<OrderDto>(Strict)
                        ?? throw new InvalidOrderException("record is null");

                    // Keep each record's raw JSON: it is stored as received.
                    orders.Add(dto.ToDomain(payload: element.GetRawText()));
                }
                catch (Exception ex) when (ex is JsonException or InvalidOrderException)
                {
                    throw Drift($"Order #{index} ({TryReadId(element) ?? "no id"}): {ex.Message}", ex);
                }

                index++;
            }

            return orders;
        }
    }

    private static OrderSourceException Drift(string message, Exception? inner = null) =>
        new(OrderSourceFailure.SchemaDrift, message, inner);

    private static string? TryReadId(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty("id", out var id)
        && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;
}
