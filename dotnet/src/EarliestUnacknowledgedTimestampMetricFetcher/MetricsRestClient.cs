using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;

namespace EarliestUnacknowledgedTimestampMetricFetcher;

/// <summary>
/// Thin client over the Azure Monitor REST API (Azure Resource Manager control plane).
/// Mirrors the calls described in the Azure Monitor REST API walkthrough:
///   {resourceUri}/providers/microsoft.insights/metricDefinitions
///   {resourceUri}/providers/microsoft.insights/metrics
/// </summary>
internal sealed class MetricsRestClient : IDisposable
{
    private const string ArmBaseUrl = "https://management.azure.com";
    private const string ApiVersion = "2024-02-01";

    private readonly HttpClient _http;
    private readonly TokenCredential _credential;
    private static readonly string[] ArmScope = { "https://management.azure.com/.default" };

    public MetricsRestClient(TokenCredential credential)
    {
        _credential = credential;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    }

    private async Task<string> AuthorizeAndSendAsync(string url, CancellationToken ct)
    {
        var token = await _credential
            .GetTokenAsync(new TokenRequestContext(ArmScope), ct)
            .ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new MetricsApiException(response.StatusCode, url, body);
        }

        return body;
    }

    /// <summary>
    /// Retrieves the raw metric definitions JSON for the given namespace.
    /// </summary>
    public Task<string> GetMetricDefinitionsAsync(
        string resourceUri,
        string metricNamespace,
        CancellationToken ct = default)
    {
        var url = $"{ArmBaseUrl}{resourceUri}/providers/microsoft.insights/metricDefinitions" +
                  $"?api-version={ApiVersion}" +
                  $"&metricnamespace={Uri.EscapeDataString(metricNamespace)}";
        return AuthorizeAndSendAsync(url, ct);
    }

    /// <summary>
    /// Retrieves the raw metric values JSON.
    /// </summary>
    public Task<string> GetMetricValuesAsync(
        string resourceUri,
        string metricNamespace,
        string metricName,
        string aggregation,
        string interval,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        string filter,
        CancellationToken ct = default)
    {
        var timespan = $"{Iso(startUtc)}/{Iso(endUtc)}";
        var url = $"{ArmBaseUrl}{resourceUri}/providers/microsoft.insights/metrics" +
                  $"?api-version={ApiVersion}" +
                  $"&metricnames={Uri.EscapeDataString(metricName)}" +
                  $"&metricnamespace={Uri.EscapeDataString(metricNamespace)}" +
                  $"&aggregation={Uri.EscapeDataString(aggregation)}" +
                  $"&interval={Uri.EscapeDataString(interval)}" +
                  $"&timespan={Uri.EscapeDataString(timespan)}" +
                  $"&$filter={Uri.EscapeDataString(filter)}";
        return AuthorizeAndSendAsync(url, ct);
    }

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Raised when the Azure Monitor REST API returns a non-success status code.
/// </summary>
internal sealed class MetricsApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string ResponseBody { get; }

    public MetricsApiException(HttpStatusCode statusCode, string url, string body)
        : base(BuildMessage(statusCode, url, body))
    {
        StatusCode = statusCode;
        ResponseBody = body;
    }

    private static string BuildMessage(HttpStatusCode statusCode, string url, string body)
    {
        var hint = statusCode switch
        {
            HttpStatusCode.Unauthorized =>
                " (401 Unauthorized: sign-in did not produce a valid token for Azure Resource Manager.)",
            HttpStatusCode.Forbidden =>
                " (403 Forbidden: the signed-in account needs at least the 'Monitoring Reader' role on the resource.)",
            HttpStatusCode.NotFound =>
                " (404 Not Found: check the Subscription ID, Resource Group, and Account name.)",
            _ => string.Empty
        };

        // Surface the top-level error message from the ARM response when available.
        var detail = TryExtractErrorMessage(body);
        return $"Azure Monitor API request failed with status {(int)statusCode} {statusCode}.{hint}" +
               (string.IsNullOrEmpty(detail) ? string.Empty : $" Details: {detail}");
    }

    private static string TryExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("message", out var message))
                {
                    return message.GetString() ?? string.Empty;
                }
            }
            else if (doc.RootElement.TryGetProperty("message", out var topMessage))
            {
                return topMessage.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // Non-JSON body; ignore.
        }

        return string.Empty;
    }
}
