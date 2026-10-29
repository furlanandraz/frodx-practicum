using OrderIngestion.Domain.Orders;

namespace OrderIngestion.Tests.TestSupport;

internal static class Sample
{
    public static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);

    public static Order Order(string id, string customerId = "CUST-1", string createdAt = "2026-10-29T10:00:00Z") => new(
        ExternalId: id,
        CustomerId: customerId,
        Status: OrderStatus.Paid,
        Currency: "EUR",
        TotalAmount: 10m,
        CreatedAt: At(createdAt),
        Lines: [new OrderLine("SKU-1", 1, 10m)],
        Payload: $$"""{"id":"{{id}}","customerId":"{{customerId}}"}""");

    public static string OrderJson(string id = "ORD-1", string status = "paid") => $$"""
        {
          "id": "{{id}}",
          "customerId": "CUST-1",
          "status": "{{status}}",
          "currency": "EUR",
          "totalAmount": 10.00,
          "createdAt": "2026-10-29T10:00:00Z",
          "lines": [ { "sku": "SKU-1", "quantity": 1, "unitPrice": 10.00 } ]
        }
        """;

    public static string Scenario(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "responses", $"{name}.json"));
}
