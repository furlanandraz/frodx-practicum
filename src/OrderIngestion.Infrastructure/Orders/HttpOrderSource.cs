using System.Globalization;
using System.Net;
using Microsoft.Extensions.Options;
using OrderIngestion.Domain.Orders;

namespace OrderIngestion.Infrastructure.Orders;

/// <summary>
/// Fetches orders from the upstream HTTP API: one attempt per cycle, no retries within it.
/// A failed attempt is retried by the next cycle, which starts from the same cursor.
/// </summary>
internal sealed class HttpOrderSource(HttpClient httpClient, IOptions<OrderApiOptions> options) : IOrderSource
{
    public async Task<FetchedOrders> FetchOrdersAsync(DateTimeOffset? since, CancellationToken cancellationToken)
    {
        var path = BuildPath(options.Value.OrdersPath, since);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.GetAsync(path, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new OrderSourceException(OrderSourceFailure.Other, $"Network error calling the order API ({path}): {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout elapsed (a real shutdown would have cancelled our own token instead).
            throw new OrderSourceException(OrderSourceFailure.Other, $"Order API did not answer within {httpClient.Timeout.TotalSeconds:F0} s ({path})", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var failure = response.StatusCode switch
                {
                    HttpStatusCode.NotFound => OrderSourceFailure.NotFound,
                    HttpStatusCode.Unauthorized => OrderSourceFailure.Unauthorized,
                    _ => OrderSourceFailure.Other,
                };

                throw new OrderSourceException(
                    failure, $"Order API returned {(int)response.StatusCode} {response.ReasonPhrase} for {path}");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return OrderPayloadReader.Read(body);
        }
    }

    // Adds since to the path. If the configured path already has a query string
    // (e.g. /orders?limit=100), since is appended with & so both parameters survive.
    private static string BuildPath(string ordersPath, DateTimeOffset? since)
    {
        if (since is null)
        {
            return ordersPath;
        }

        var separator = ordersPath.Contains('?') ? '&' : '?';
        var value = Uri.EscapeDataString(since.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        return $"{ordersPath}{separator}since={value}";
    }
}
