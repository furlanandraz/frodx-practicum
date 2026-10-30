namespace OrderIngestion.Domain.Orders;

/// <summary>
/// Where orders come from. Today an HTTP API; could be a file drop without the ingestion logic changing.
/// </summary>
public interface IOrderSource
{
    /// <param name="since">Only orders created at or after this time; null fetches everything.</param>
    /// <returns>The valid orders, plus the ones skipped because a value failed validation.</returns>
    /// <exception cref="OrderSourceException">The fetch failed, or the response doesn't match our schema.</exception>
    Task<FetchedOrders> FetchOrdersAsync(DateTimeOffset? since, CancellationToken cancellationToken);
}
