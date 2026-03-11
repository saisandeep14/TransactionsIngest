using TransactionsIngest.Models;

namespace TransactionsIngest.Services;

/// <summary>
/// Abstraction over the data source (real HTTP API or local mock feed).
/// </summary>
public interface ITransactionFetcher
{
    /// <summary>
    /// Returns all transactions from the last-24-hour snapshot.
    /// The list may arrive unordered; callers must not assume any ordering.
    /// </summary>
    Task<IReadOnlyList<TransactionDto>> FetchAsync(CancellationToken ct = default);
}
