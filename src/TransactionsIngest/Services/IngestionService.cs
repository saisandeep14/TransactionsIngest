using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TransactionsIngest.Configuration;
using TransactionsIngest.Data;
using TransactionsIngest.Models;

namespace TransactionsIngest.Services;

public sealed class IngestionService
{
    private readonly TransactionsDbContext _db;
    private readonly ITransactionFetcher _fetcher;
    private readonly AppSettings _settings;
    private readonly ILogger<IngestionService> _logger;

    public IngestionService(
        TransactionsDbContext db,
        ITransactionFetcher fetcher,
        IOptions<AppSettings> settings,
        ILogger<IngestionService> logger)
    {
        _db = db;
        _fetcher = fetcher;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var incoming = await _fetcher.FetchAsync(ct);
        var now = DateTime.UtcNow;
        var cutoff = now.AddHours(-_settings.SnapshotWindowHours);

        _logger.LogInformation(
            "Run started at {Now:u}. Window cutoff: {Cutoff:u}. Incoming: {Count} record(s).",
            now, cutoff, incoming.Count);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // last entry wins if the same TransactionId appears more than once
            var incomingById = incoming
                .GroupBy(d => d.TransactionId)
                .ToDictionary(g => g.Key, g => g.Last());

            var windowIds = incomingById.Keys.ToList();

            var dbRecords = await _db.Transactions
                .Where(t => windowIds.Contains(t.TransactionId)
                         || (t.TransactionTime >= cutoff
                             && t.Status != TransactionStatus.Finalized))
                .ToDictionaryAsync(t => t.TransactionId, ct);

            var auditsToAdd = new List<TransactionAudit>();
            int inserted = 0, updated = 0;

            foreach (var (id, dto) in incomingById)
            {
                if (dbRecords.TryGetValue(id, out var existing))
                {
                    if (existing.Status == TransactionStatus.Finalized)
                    {
                        _logger.LogDebug("Skipping finalized record {Id}.", id);
                        continue;
                    }

                    var changes = DetectChanges(existing, dto);
                    if (changes.Count > 0)
                    {
                        ApplyDto(existing, dto);
                        existing.Status = TransactionStatus.Active;
                        existing.UpdatedAt = now;

                        auditsToAdd.Add(new TransactionAudit
                        {
                            TransactionId = id,
                            ChangeType = ChangeTypes.Update,
                            ChangeDetail = string.Join("; ", changes),
                            ChangedAt = now
                        });
                        updated++;
                    }
                    else if (existing.Status == TransactionStatus.Revoked)
                    {
                        // re-appearing revoked transaction with no field changes
                        existing.Status = TransactionStatus.Active;
                        existing.UpdatedAt = now;
                        auditsToAdd.Add(new TransactionAudit
                        {
                            TransactionId = id,
                            ChangeType = ChangeTypes.Update,
                            ChangeDetail = "Status: Revoked -> Active (re-appeared in snapshot)",
                            ChangedAt = now
                        });
                        updated++;
                    }
                }
                else
                {
                    var record = new TransactionRecord
                    {
                        TransactionId = dto.TransactionId,
                        CardLast4 = ExtractLast4(dto.CardNumber),
                        LocationCode = Truncate(dto.LocationCode, 20),
                        ProductName = Truncate(dto.ProductName, 20),
                        Amount = dto.Amount,
                        TransactionTime = DateTime.SpecifyKind(dto.Timestamp, DateTimeKind.Utc),
                        Status = TransactionStatus.Active,
                        CreatedAt = now,
                        UpdatedAt = now
                    };
                    _db.Transactions.Add(record);
                    dbRecords[id] = record;

                    auditsToAdd.Add(new TransactionAudit
                    {
                        TransactionId = id,
                        ChangeType = ChangeTypes.Insert,
                        ChangeDetail = $"Amount={dto.Amount:F2}, Location={dto.LocationCode}, Product={dto.ProductName}",
                        ChangedAt = now
                    });
                    inserted++;
                }
            }

            // revoke active records that are within the window but missing from the snapshot
            int revoked = 0;
            foreach (var (id, record) in dbRecords)
            {
                if (record.Status != TransactionStatus.Active) continue;
                if (record.TransactionTime < cutoff) continue;
                if (incomingById.ContainsKey(id)) continue;

                record.Status = TransactionStatus.Revoked;
                record.UpdatedAt = now;
                auditsToAdd.Add(new TransactionAudit
                {
                    TransactionId = id,
                    ChangeType = ChangeTypes.Revoke,
                    ChangeDetail = "Absent from current 24-hour snapshot.",
                    ChangedAt = now
                });
                revoked++;
            }

            // finalize any active records older than the snapshot window
            int finalized = 0;
            var oldActiveRecords = await _db.Transactions
                .Where(t => t.TransactionTime < cutoff && t.Status == TransactionStatus.Active)
                .ToListAsync(ct);

            foreach (var record in oldActiveRecords)
            {
                record.Status = TransactionStatus.Finalized;
                record.UpdatedAt = now;
                auditsToAdd.Add(new TransactionAudit
                {
                    TransactionId = record.TransactionId,
                    ChangeType = ChangeTypes.Finalize,
                    ChangeDetail = "Transaction time outside 24-hour window.",
                    ChangedAt = now
                });
                finalized++;
            }

            _db.Audits.AddRange(auditsToAdd);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "Run complete. Inserted={Inserted}, Updated={Updated}, Revoked={Revoked}, Finalized={Finalized}, AuditRows={Audit}.",
                inserted, updated, revoked, finalized, auditsToAdd.Count);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            _logger.LogError("Run failed — transaction rolled back.");
            throw;
        }
    }

    private static List<string> DetectChanges(TransactionRecord existing, TransactionDto dto)
    {
        var changes = new List<string>();
        var incomingLast4 = ExtractLast4(dto.CardNumber);
        var incomingTime = DateTime.SpecifyKind(dto.Timestamp, DateTimeKind.Utc);

        if (existing.CardLast4 != incomingLast4)
            changes.Add($"CardLast4: {existing.CardLast4} -> {incomingLast4}");
        if (existing.LocationCode != Truncate(dto.LocationCode, 20))
            changes.Add($"LocationCode: {existing.LocationCode} -> {dto.LocationCode}");
        if (existing.ProductName != Truncate(dto.ProductName, 20))
            changes.Add($"ProductName: {existing.ProductName} -> {dto.ProductName}");
        if (existing.Amount != dto.Amount)
            changes.Add($"Amount: {existing.Amount:F2} -> {dto.Amount:F2}");
        if (existing.TransactionTime != incomingTime)
            changes.Add($"TransactionTime: {existing.TransactionTime:u} -> {incomingTime:u}");

        return changes;
    }

    private static void ApplyDto(TransactionRecord record, TransactionDto dto)
    {
        record.CardLast4 = ExtractLast4(dto.CardNumber);
        record.LocationCode = Truncate(dto.LocationCode, 20);
        record.ProductName = Truncate(dto.ProductName, 20);
        record.Amount = dto.Amount;
        record.TransactionTime = DateTime.SpecifyKind(dto.Timestamp, DateTimeKind.Utc);
    }

    private static string ExtractLast4(string cardNumber)
    {
        var digits = new string(cardNumber.Where(char.IsDigit).ToArray());
        return digits.Length >= 4 ? digits[^4..] : digits.PadLeft(4, '*');
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}