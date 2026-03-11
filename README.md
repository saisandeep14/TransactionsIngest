# TransactionsIngest

A .NET 9 console application that ingests the last-24-hour transaction snapshot from a retail payments API, upserts records, detects field-level changes, revokes missing records, and finalizes aged-out records — all inside a single idempotent database transaction per run.

---

## Prerequisites

| Requirement | Version |
|---|---|
| .NET SDK | **9.0** or later |
| Operating System | Windows, macOS, or Linux |

Download the SDK from: <https://dotnet.microsoft.com/download/dotnet/9.0>

Verify installation:
```bash
dotnet --version
# Should print: 9.x.x
```

---

## Project Structure

```
TransactionsIngest/
├── src/
│   └── TransactionsIngest/
│       ├── Configuration/
│       │   └── AppSettings.cs          # Typed config class
│       ├── Data/
│       │   ├── TransactionsDbContext.cs # EF Core DbContext
│       │   └── Migrations/             # Auto-applied on startup
│       ├── Models/
│       │   ├── TransactionDto.cs       # API/JSON shape
│       │   ├── TransactionRecord.cs    # DB entity
│       │   ├── TransactionAudit.cs     # Audit log entity
│       │   └── TransactionStatus.cs   # Active / Revoked / Finalized
│       ├── Services/
│       │   ├── ITransactionFetcher.cs  # Fetcher abstraction
│       │   ├── HttpTransactionFetcher.cs # Real HTTP client
│       │   ├── MockTransactionFetcher.cs # Local JSON file reader
│       │   └── IngestionService.cs     # Core business logic
│       ├── appsettings.json            # Configuration
│       ├── mock-feed.json              # Sample data for local testing
│       └── Program.cs                  # Entry point / DI setup
└── tests/
    └── TransactionsIngest.Tests/
        ├── TestFactory.cs              # In-memory DB helpers
        └── IngestionServiceTests.cs    # xUnit tests
```

---

## Step-by-Step: Running the Application

### Step 1 — Clone or unzip the project

```bash
cd /path/to/where/you/want/it
# If you received a zip:
unzip TransactionsIngest.zip
cd TransactionsIngest
```

### Step 2 — Restore NuGet packages

```bash
dotnet restore
```

### Step 3 — Review configuration (optional)

Open `src/TransactionsIngest/appsettings.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=transactions.db"
  },
  "AppSettings": {
    "ApiBaseUrl": "https://api.example.com",
    "ApiPath": "/transactions/last24h",
    "MockFeedPath": "mock-feed.json",
    "SnapshotWindowHours": 24
  }
}
```

- **MockFeedPath** is set to `mock-feed.json` by default so the app runs without a real API.
- To use the real API, remove the `MockFeedPath` line (or set it to `""`) and update `ApiBaseUrl`/`ApiPath`.
- The SQLite database file (`transactions.db`) is created automatically in the working directory on first run.

### Step 4 — Run the application

```bash
dotnet run --project src/TransactionsIngest
```

Expected console output (first run):
```
info: Applying database migrations...
info: Reading mock feed from mock-feed.json
info: Loaded 6 transaction(s) from mock feed.
info: Run started at 2026-03-09 12:00:00Z. Window cutoff: 2026-03-08 12:00:00Z. Incoming: 6 record(s).
info: Run complete. Inserted=6, Updated=0, Revoked=0, Finalized=0, AuditRows=6.
info: Ingestion run finished successfully.
```

Run it a **second time** (idempotency check — no new rows):
```
info: Run complete. Inserted=0, Updated=0, Revoked=0, Finalized=0, AuditRows=0.
```

### Step 5 — Inspect the database (optional)

Install the SQLite CLI or use [DB Browser for SQLite](https://sqlitebrowser.org/dl/):

```bash
sqlite3 src/TransactionsIngest/transactions.db

# Inside SQLite:
.headers on
.mode column
SELECT TransactionId, CardLast4, ProductName, Amount, Status FROM Transactions;
SELECT * FROM Audits ORDER BY Id;
.quit
```

---

## Running Tests

```bash
dotnet test
```

All 13 tests cover:

| Scenario | Test |
|---|---|
| New transaction inserted | `NewTransaction_IsInserted_WithAuditRow` |
| Amount change detected | `ChangedAmount_IsDetected_AndAudited` |
| Location change detected | `ChangedLocation_IsDetected_AndAudited` |
| Idempotent run — no duplicate audits | `IdenticalRun_ProducesNoNewAuditRows` |
| Idempotent run — no duplicate records | `IdenticalRun_ProducesNoDuplicateTransactionRows` |
| Missing record is revoked | `ActiveRecord_AbsentInNextSnapshot_IsRevoked` |
| Revoked record re-activated | `RevokedRecord_ReappearsInSnapshot_IsReActivated` |
| Old record is finalized | `ActiveRecord_OlderThanWindow_IsFinalized` |
| Finalized record never changes | `FinalizedRecord_IsNotModifiedBySubsequentRuns` |
| Multiple field changes in one audit row | `MultipleChangedFields_AllCapturedInSingleAuditRow` |
| PAN is never stored — only last 4 digits | `FullCardNumber_IsNeverPersisted_OnlyLast4Stored` |

---

## Simulating Different Scenarios

### Simulate an update
Edit `mock-feed.json`, change the `amount` of `T-1001` from `19.99` to `24.99`, then run again:
```bash
dotnet run --project src/TransactionsIngest
# → Updated=1, AuditRows=1 with "Amount: 19.99 -> 24.99"
```

### Simulate a revocation
Remove a transaction entry from `mock-feed.json`, then run again:
```bash
dotnet run --project src/TransactionsIngest
# → Revoked=1
```

### Reset state (start fresh)
```bash
rm src/TransactionsIngest/transactions.db
dotnet run --project src/TransactionsIngest
```

---

## Design Decisions & Assumptions

### Privacy
The full card PAN is never persisted. Only the last 4 digits are stored (`CardLast4`), extracted at ingest time.

### Idempotency
Each run executes inside a **single SQLite transaction**. If two fields change, both are captured in a single `Update` audit row (not two separate rows). A repeated run with the same input adds zero rows.

### Revocation vs. Finalization
- **Revoked**: the transaction is *within* the 24-hour window but absent from the current snapshot. It may reappear in a later run (e.g. network delay) and will be re-activated.
- **Finalized**: the transaction is *older than* the 24-hour window. It will never be updated again.

### Duplicate TransactionId in snapshot
If the upstream API returns the same `TransactionId` more than once in one batch, the last occurrence wins (last-write-wins within a single snapshot).

### String truncation
Fields `LocationCode` and `ProductName` are truncated to 20 characters if the API returns longer values, matching the DB column constraint.

### EF Core migrations
The migration is checked and auto-applied at startup (`db.Database.MigrateAsync()`). No manual `dotnet ef` commands needed to run the app.

---

## Build for Production (Release mode)

```bash
dotnet publish src/TransactionsIngest -c Release -o ./publish
cd publish
dotnet TransactionsIngest.dll
```

---

## Scheduler Integration

The app is designed for single-run execution. To run it hourly, register it with:

- **Windows**: Task Scheduler (Action: `dotnet`, Arguments: `TransactionsIngest.dll`)
- **Linux/macOS**: cron
  ```cron
  0 * * * * cd /path/to/publish && dotnet TransactionsIngest.dll >> /var/log/ingest.log 2>&1
  ```
