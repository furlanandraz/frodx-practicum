namespace OrderIngestion.Domain.Orders;

/// <summary>What one fetch returned.</summary>
/// <param name="Orders">Orders that passed validation. These get stored.</param>
/// <param name="Skipped">
/// Orders with a value we don't accept (a null, an unknown status, ...). Logged and not stored;
/// the rest of the batch is unaffected.
/// </param>
public sealed record FetchedOrders(IReadOnlyList<Order> Orders, IReadOnlyList<SkippedOrder> Skipped);

/// <param name="Index">Position in the response, to find the record in the raw payload.</param>
/// <param name="ExternalId">The order ID, if it could be read.</param>
public sealed record SkippedOrder(int Index, string? ExternalId, string Reason);
