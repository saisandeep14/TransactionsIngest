using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TransactionsIngest.Configuration;
using TransactionsIngest.Data;
using TransactionsIngest.Models;
using TransactionsIngest.Services;

namespace TransactionsIngest.Tests;

internal static class TestFactory
{
    public static TransactionsDbContext CreateDb(string? dbName = null)
    {
        var opts = new DbContextOptionsBuilder<TransactionsDbContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new TransactionsDbContext(opts);
    }

    public static IngestionService CreateService(
        TransactionsDbContext db,
        IEnumerable<TransactionDto> feed,
        int windowHours = 24)
    {
        var fetcher  = new StaticFetcher(feed.ToList());
        var settings = Options.Create(new AppSettings { SnapshotWindowHours = windowHours });
        return new IngestionService(db, fetcher, settings, NullLogger<IngestionService>.Instance);
    }

    public static TransactionDto MakeDto(
        string id,
        decimal amount      = 10.00m,
        string card         = "4111111111111111",
        string location     = "STO-01",
        string product      = "Widget",
        DateTime? timestamp = null) => new()
    {
        TransactionId = id,
        CardNumber    = card,
        LocationCode  = location,
        ProductName   = product,
        Amount        = amount,
        Timestamp     = timestamp ?? DateTime.UtcNow.AddHours(-1)
    };

    private sealed class StaticFetcher : ITransactionFetcher
    {
        private readonly IReadOnlyList<TransactionDto> _feed;
        public StaticFetcher(IReadOnlyList<TransactionDto> feed) => _feed = feed;
        public Task<IReadOnlyList<TransactionDto>> FetchAsync(CancellationToken ct = default)
            => Task.FromResult(_feed);
    }
}
