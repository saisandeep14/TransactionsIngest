using System.Text.Json.Serialization;

namespace TransactionsIngest.Models;

/// <summary>
/// Mirrors the JSON shape returned by the transactions API (or mock feed).
/// </summary>
public sealed class TransactionDto
{
    [JsonPropertyName("transactionId")]
    public string TransactionId { get; init; } = null!;

    [JsonPropertyName("cardNumber")]
    public string CardNumber { get; init; } = null!;

    [JsonPropertyName("locationCode")]
    public string LocationCode { get; init; } = null!;

    [JsonPropertyName("productName")]
    public string ProductName { get; init; } = null!;

    [JsonPropertyName("amount")]
    public decimal Amount { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; }
}
