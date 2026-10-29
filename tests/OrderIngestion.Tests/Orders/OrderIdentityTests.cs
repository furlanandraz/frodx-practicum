using OrderIngestion.Domain.Orders;

namespace OrderIngestion.Tests.Orders;

public sealed class OrderIdentityTests
{
    [Fact]
    public void Same_order_and_customer_give_the_same_hash()
    {
        Assert.Equal(
            OrderIdentity.CanonicalHash("ORD-1", "CUST-1"),
            OrderIdentity.CanonicalHash("ORD-1", "CUST-1"));
    }

    [Fact]
    public void Hash_is_64_lowercase_hex_characters()
    {
        var hash = OrderIdentity.CanonicalHash("ORD-1", "CUST-1");

        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void Same_order_id_for_a_different_customer_is_a_different_order()
    {
        Assert.NotEqual(
            OrderIdentity.CanonicalHash("ORD-1", "CUST-1"),
            OrderIdentity.CanonicalHash("ORD-1", "CUST-2"));
    }

    [Fact]
    public void Parts_cannot_bleed_into_each_other()
    {
        // Naive concatenation would make both "ABC".
        Assert.NotEqual(
            OrderIdentity.CanonicalHash("AB", "C"),
            OrderIdentity.CanonicalHash("A", "BC"));
    }

    [Fact]
    public void Surrounding_whitespace_is_ignored()
    {
        Assert.Equal(
            OrderIdentity.CanonicalHash("ORD-1", "CUST-1"),
            OrderIdentity.CanonicalHash(" ORD-1 ", "CUST-1\t"));
    }
}
