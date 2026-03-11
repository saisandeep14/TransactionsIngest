using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TransactionsIngest.Configuration;
using TransactionsIngest.Models;

namespace TransactionsIngest.Services;

public sealed class MockTransactionFetcher : ITransactionFetcher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;
    private readonly ILogger<MockTransactionFetcher> _logger;

    public MockTransactionFetcher(
        IOptions<AppSettings> settings,
        ILogger<MockTransactionFetcher> logger)
    {
        _filePath = settings.Value.MockFeedPath
            ?? throw new InvalidOperationException("MockFeedPath must be set when using MockTransactionFetcher.");
        _logger = logger;
    }

    public async Task<IReadOnlyList<TransactionDto>> FetchAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Reading mock feed from {Path}", _filePath);

        await using var stream = File.OpenRead(_filePath);
        var result = await JsonSerializer.DeserializeAsync<List<TransactionDto>>(stream, JsonOptions, ct)
            ?? throw new InvalidOperationException("Mock feed file is empty or null.");

        _logger.LogInformation("Loaded {Count} transaction(s) from mock feed.", result.Count);
        return result;
    }
}