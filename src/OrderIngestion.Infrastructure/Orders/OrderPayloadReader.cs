using System.Text.Json;
using OrderIngestion.Domain.Orders;

namespace OrderIngestion.Infrastructure.Orders;

/// <summary>
/// Checks a response body against our schema and turns it into orders. Each order goes through
/// three steps:
/// <list type="number">
///   <item><b>Nulls:</b> we don't know yet which fields the API may leave empty, so we accept none.
///   Checked before parsing, so a null skips only its order.</item>
///   <item><b>Shape</b> (a field missing, added or of another JSON type): schema drift. The API changed,
///   so nothing from the response is used.</item>
///   <item><b>Values</b> (an unknown status, a quantity of 0, ...): that order is skipped.</item>
/// </list>
/// A skipped order doesn't affect the rest of the batch.
/// </summary>
internal static class OrderPayloadReader
{
    /// <summary>
    /// .NET's built-in strict mode (every field required, no unknown or duplicate fields), with
    /// camelCase names: the API sends customerId for our CustomerId.
    /// </summary>
    private static readonly JsonSerializerOptions Strict = new(JsonSerializerOptions.Strict)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <exception cref="OrderSourceException">With <see cref="OrderSourceFailure.SchemaDrift"/>.</exception>
    public static FetchedOrders Read(string json)
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
            var skipped = new List<SkippedOrder>();
            var index = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var id = TryReadId(element);

                if (FindNull(element, path: "") is { } nullField)
                {
                    skipped.Add(new SkippedOrder(index, id, nullField.Length == 0 ? "order is null" : $"{nullField} is null"));
                    index++;
                    continue;
                }

                OrderDto dto;
                try
                {
                    dto = element.Deserialize<OrderDto>(Strict)!;
                }
                catch (JsonException ex)
                {
                    throw Drift($"Order #{index} ({id ?? "no id"}): {ex.Message}", ex);
                }

                try
                {
                    orders.Add(dto.ToDomain(payload: element.GetRawText()));
                }
                catch (InvalidOrderException ex)
                {
                    skipped.Add(new SkippedOrder(index, id, ex.Message));
                }

                index++;
            }

            return new FetchedOrders(orders, skipped);
        }
    }

    /// <summary>Path of the first null (e.g. "lines[0].quantity"), "" if the element itself is null, or null if there is none.</summary>
    private static string? FindNull(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return path;

            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var propertyPath = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
                    if (FindNull(property.Value, propertyPath) is { } found)
                    {
                        return found;
                    }
                }

                return null;

            case JsonValueKind.Array:
                var position = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (FindNull(item, $"{path}[{position}]") is { } found)
                    {
                        return found;
                    }

                    position++;
                }

                return null;

            default:
                return null;
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
