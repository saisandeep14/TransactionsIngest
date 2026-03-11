using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TransactionsIngest.Configuration;
using TransactionsIngest.Models;

namespace TransactionsIngest.Services;

public sealed class HttpTransactionFetcher : ITransactionFetcher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly AppSettings _settings;
    private readonly ILogger<HttpTransactionFetcher> _logger;

    public HttpTransactionFetcher(
        HttpClient http,
        IOptions<AppSettings> settings,
        ILogger<HttpTransactionFetcher> logger)
    {
        _http = http;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<TransactionDto>> FetchAsync(CancellationToken ct = default)
    {
        var url = _settings.ApiBaseUrl.TrimEnd('/') + "/" + _settings.ApiPath.TrimStart('/');
        _logger.LogInformation("Fetching transactions from {Url}", url);

        var result = await _http.GetFromJsonAsync<List<TransactionDto>>(url, JsonOptions, ct)
            ?? throw new InvalidOperationException("API returned null payload.");

        _logger.LogInformation("Fetched {Count} transaction(s) from API.", result.Count);
        return result;
    }
}