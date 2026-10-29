namespace OrderIngestion.Domain.Orders;

/// <summary>Why a fetch failed. The first three need a person and are emailed; the last one isn't.</summary>
public enum OrderSourceFailure
{
    /// <summary>404: the endpoint is wrong or gone (their side).</summary>
    NotFound,

    /// <summary>401: our credentials are refused, possibly stale (our side).</summary>
    Unauthorized,

    /// <summary>
    /// The API answered, but the response doesn't match our predefined schema (not JSON, not a list,
    /// a field missing, added or of the wrong type, an unknown value). Nothing from it is stored.
    /// </summary>
    SchemaDrift,

    /// <summary>Anything else: timeout, network error, 5xx, other 4xx. The next cycle tries again.</summary>
    Other,
}

/// <summary>
/// The fetch failed. Thrown by <see cref="IOrderSource"/> adapters so the domain sees one kind of
/// error instead of HTTP- or JSON-specific ones.
/// </summary>
public sealed class OrderSourceException(OrderSourceFailure failure, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public OrderSourceFailure Failure { get; } = failure;
}
