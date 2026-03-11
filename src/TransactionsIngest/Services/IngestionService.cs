using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TransactionsIngest.Configuration;
using TransactionsIngest.Data;
using TransactionsIngest.Models;

namespace TransactionsIngest.Services;

/// <summary>
/// Orchestrates the hourly ingestion run:
///   1. Fetch the 24-hour snapshot.
///   2. Upsert each transaction (insert new / detect and record changes).
///   3. Revoke active records that are absent from the snapshot but still within 24 h.
///   4. Finalize records older than 24 h.
///
/// The entire run executes inside a single database transaction to guarantee
/// idempotency: a repeated run with the same input produces no duplicate rows.
/// </summary>
public sealed class IngestionService
{
    private readonly TransactionsDbContext _db;
    private readonly ITransactionFetcher   _fetcher;
    private readonly AppSettings           _settings;
    private readonly ILogger<IngestionService> _logger;

    public IngestionService(
        TransactionsDbContext db,
        ITransactionFetcher fetcher,
        IOptions<AppSettings> settings,
        ILogger<IngestionService> logger)
    {
        _db       = db;
        _fetcher  = fetcher;
        _settings = settings.Value;
        _logger   = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        // ── 1. Fetch snapshot ──────────────────────────────────────────────────
        var incoming = await _fetcher.FetchAsync(ct);
        var now      = DateTime.UtcNow;
        var cutoff   = now.AddHours(-_settings.SnapshotWindowHours);

        _logger.LogInformation(
            "Run started at {Now:u}. Window cutoff: {Cutoff:u}. Incoming: {Count} record(s).",
            now, cutoff, incoming.Count);

        // ── 2. Single database transaction (idempotency) ───────────────────────
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            var incomingById = incoming
                .GroupBy(d => d.TransactionId)
                .ToDictionary(g => g.Key, g => g.Last()); // last wins on duplicates

            // Load all DB records whose TransactionId appears in the snapshot
            // OR that are still Active/Revoked within the window (for revocation check).
            var windowIds = incomingById.Keys.ToList();

            var dbRecords = await _db.Transactions
                .Where(t => windowIds.Contains(t.TransactionId)
                         || (t.TransactionTime >= cutoff
                             && t.Status != TransactionStatus.Finalized))
                .ToDictionaryAsync(t => t.TransactionId, ct);

            var auditsToAdd = new List<TransactionAudit>();

            // ── 3. Upsert ──────────────────────────────────────────────────────
            int inserted = 0, updated = 0;
            foreach (var (id, dto) in incomingById)
            {
                if (dbRecords.TryGetValue(id, out var existing))
                {
                    // Already finalized → skip silently (idempotent)
                    if (existing.Status == TransactionStatus.Finalized)
                    {
                        _logger.LogDebug("Skipping finalized record {Id}.", id);
                        continue;
                    }

                    var changes = DetectChanges(existing, dto);
                    if (changes.Count > 0)
                    {
                        // Apply changes
                        ApplyDto(existing, dto);
                        existing.Status    = TransactionStatus.Active; // un-revoke if re-appearing
                        existing.UpdatedAt = now;

                        auditsToAdd.Add(new TransactionAudit
                        {
                            TransactionId = id,
                            ChangeType    = ChangeTypes.Update,
                            ChangeDetail  = string.Join("; ", changes),
                            ChangedAt     = now
                        });
                        updated++;
                    }
                    else if (existing.Status == TransactionStatus.Revoked)
                    {
                        // Re-appearing revoked transaction with no field changes → un-revoke
                        existing.Status    = TransactionStatus.Active;
                        existing.UpdatedAt = now;
                        auditsToAdd.Add(new TransactionAudit
                        {
                            TransactionId = id,
                            ChangeType    = ChangeTypes.Update,
                            ChangeDetail  = "Status: Revoked -> Active (re-appeared in snapshot)",
                            ChangedAt     = now
                        });
                        updated++;
                    }
                    // else: no changes, no audit row → idempotent
                }
                else
                {
                    // New record
                    var record = new TransactionRecord
                    {
                        TransactionId   = dto.TransactionId,
                        CardLast4       = ExtractLast4(dto.CardNumber),
                        LocationCode    = Truncate(dto.LocationCode, 20),
                        ProductName     = Truncate(dto.ProductName, 20),
                        Amount          = dto.Amount,
                        TransactionTime = DateTime.SpecifyKind(dto.Timestamp, DateTimeKind.Utc),
                        Status          = TransactionStatus.Active,
                        CreatedAt       = now,
                        UpdatedAt       = now
                    };
                    _db.Transactions.Add(record);
                    dbRecords[id] = record; // keep dict consistent for revocation step

                    auditsToAdd.Add(new TransactionAudit
                    {
                        TransactionId = id,
                        ChangeType    = ChangeTypes.Insert,
                        ChangeDetail  = $"Amount={dto.Amount:F2}, Location={dto.LocationCode}, Product={dto.ProductName}",
                        ChangedAt     = now
                    });
                    inserted++;
                }
            }

            // ── 4. Revocation ──────────────────────────────────────────────────
            // Active records within window that are absent from the current snapshot.
            int revoked = 0;
            foreach (var (id, record) in dbRecords)
            {
                if (record.Status != TransactionStatus.Active) continue;
                if (record.TransactionTime < cutoff)           continue; // outside window
                if (incomingById.ContainsKey(id))              continue; // present → no revoke

                record.Status    = TransactionStatus.Revoked;
                record.UpdatedAt = now;
                auditsToAdd.Add(new TransactionAudit
                {
                    TransactionId = id,
                    ChangeType    = ChangeTypes.Revoke,
                    ChangeDetail  = "Absent from current 24-hour snapshot.",
                    ChangedAt     = now
                });
                revoked++;
            }

            // ── 5. Finalization (optional) ─────────────────────────────────────
            // Any Active record older than the window is finalized.
            int finalized = 0;
            var oldActiveRecords = await _db.Transactions
                .Where(t => t.TransactionTime < cutoff
                         && t.Status == TransactionStatus.Active)
                .ToListAsync(ct);

            foreach (var record in oldActiveRecords)
            {
                record.Status    = TransactionStatus.Finalized;
                record.UpdatedAt = now;
                auditsToAdd.Add(new TransactionAudit
                {
                    TransactionId = record.TransactionId,
                    ChangeType    = ChangeTypes.Finalize,
                    ChangeDetail  = "Transaction time outside 24-hour window.",
                    ChangedAt     = now
                });
                finalized++;
            }

            // ── 6. Persist ────────────────────────────────────────────────────
            _db.Audits.AddRange(auditsToAdd);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "Run complete. Inserted={Inserted}, Updated={Updated}, " +
                "Revoked={Revoked}, Finalized={Finalized}, AuditRows={Audit}.",
                inserted, updated, revoked, finalized, auditsToAdd.Count);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            _logger.LogError("Run failed — transaction rolled back.");
            throw;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static List<string> DetectChanges(TransactionRecord existing, TransactionDto dto)
    {
        var changes = new List<string>();
        var incomingLast4 = ExtractLast4(dto.CardNumber);
        var incomingTime  = DateTime.SpecifyKind(dto.Timestamp, DateTimeKind.Utc);

        if (existing.CardLast4       != incomingLast4)
            changes.Add($"CardLast4: {existing.CardLast4} -> {incomingLast4}");
        if (existing.LocationCode    != Truncate(dto.LocationCode, 20))
            changes.Add($"LocationCode: {existing.LocationCode} -> {dto.LocationCode}");
        if (existing.ProductName     != Truncate(dto.ProductName, 20))
            changes.Add($"ProductName: {existing.ProductName} -> {dto.ProductName}");
        if (existing.Amount          != dto.Amount)
            changes.Add($"Amount: {existing.Amount:F2} -> {dto.Amount:F2}");
        if (existing.TransactionTime != incomingTime)
            changes.Add($"TransactionTime: {existing.TransactionTime:u} -> {incomingTime:u}");

        return changes;
    }

    private static void ApplyDto(TransactionRecord record, TransactionDto dto)
    {
        record.CardLast4       = ExtractLast4(dto.CardNumber);
        record.LocationCode    = Truncate(dto.LocationCode, 20);
        record.ProductName     = Truncate(dto.ProductName, 20);
        record.Amount          = dto.Amount;
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
