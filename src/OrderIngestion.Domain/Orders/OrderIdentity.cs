using System.Security.Cryptography;
using System.Text;

namespace OrderIngestion.Domain.Orders;

/// <summary>Decides when two orders are "the same record".</summary>
public static class OrderIdentity
{
    /// <summary>
    /// SHA-256 (lowercase hex, 64 chars) of the order ID and customer ID.
    /// </summary>
    /// <remarks>
    /// Each part is length-prefixed ("7:ORD-100|8:CUST-001") so different pairs can never
    /// produce the same input: ("AB","C") and ("A","BC") hash differently. Surrounding
    /// whitespace is ignored, so " ORD-1" and "ORD-1" are the same order.
    /// </remarks>
    public static string CanonicalHash(string externalId, string customerId)
    {
        var id = externalId.Trim();
        var customer = customerId.Trim();
        var canonical = $"{id.Length}:{id}|{customer.Length}:{customer}";

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
