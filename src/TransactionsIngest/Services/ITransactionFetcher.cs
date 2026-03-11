using TransactionsIngest.Models;

namespace TransactionsIngest.Services;

public interface ITransactionFetcher
{
    Task<IReadOnlyList<TransactionDto>> FetchAsync(CancellationToken ct = default);
}