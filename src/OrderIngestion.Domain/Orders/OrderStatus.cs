namespace OrderIngestion.Domain.Orders;

public enum OrderStatus
{
    Pending,
    Paid,
    Shipped,
    Delivered,
    Cancelled,
}
