# TransactionsIngest

A **.NET 10 Console Application** that runs hourly to ingest a 24-hour snapshot of retail payment transactions, upsert records, detect field-level changes, revoke missing transactions, and finalize aged-out records — all inside a single idempotent database transaction per run.

---

## Table of Contents

- [Background](#background)
- [Approach & Architecture](#approach--architecture)
- [Project Structure](#project-structure)
- [Data Model](#data-model)
- [How It Works](#how-it-works)
- [Prerequisites](#prerequisites)
- [Build & Run Steps](#build--run-steps)
- [Running the Tests](#running-the-tests)
- [Configuration](#configuration)
- [Simulating Scenarios](#simulating-scenarios)
- [Design Decisions & Assumptions](#design-decisions--assumptions)

---

## Background

A retail payments platform consolidates card transactions from multiple point-of-sale systems. Each upstream system pushes transactions to a gateway API, but due to network delays and batching, events may arrive out of order and hours after they occur.

This console app is designed to be triggered once per hour by an external scheduler. It:
- Fetches the last 24-hour snapshot from the transactions API
- Upserts records by `TransactionId`
- Detects and records any field-level changes
- Revokes transactions that have disappeared from the snapshot
- Finalizes records older than 24 hours so they can never change

---

## Approach & Architecture

```
Program.cs                  ← Entry point: DI setup, migrations, single run
│
├── IngestionService         ← Core business logic (upsert, revoke, finalize)
│   ├── ITransactionFetcher  ← Abstraction over data source
│   │   ├── MockTransactionFetcher   ← Reads from local JSON (dev/testing)
│   │   └── HttpTransactionFetcher   ← Calls real API (production)
│   └── TransactionsDbContext        ← EF Core + SQLite
│       ├── Transactions table       ← One row per transaction
│       └── Audits table             ← Append-only change log
```

**Key design choices:**
- **Single DB transaction per run** — the entire run commits or rolls back atomically, guaranteeing idempotency
- **Interface abstraction** — `ITransactionFetcher` allows swapping between mock and real API via config with zero code changes
- **Append-only audit log** — every insert, update, revoke, and finalize writes an immutable row to the `Audits` table
- **Privacy first** — full card PANs are never persisted; only the last 4 digits are stored

---

## Project Structure

```
TransactionsIngest/
├── TransactionsIngest.sln
├── README.md
├── mock-feed.json                        ← Sample data for local testing
│
├── src/
│   └── TransactionsIngest/
│       ├── Configuration/
│       │   └── AppSettings.cs            ← Typed config mapped from appsettings.json
│       ├── Data/
│       │   ├── TransactionsDbContext.cs  ← EF Core DbContext
│       │   └── Migrations/               ← Code-first migrations (auto-applied)
│       ├── Models/
│       │   ├── TransactionDto.cs         ← JSON shape from API
│       │   ├── TransactionRecord.cs      ← Transactions table entity
│       │   ├── TransactionAudit.cs       ← Audits table entity + ChangeTypes constants
│       │   └── TransactionStatus.cs      ← Enum: Active / Revoked / Finalized
│       ├── Services/
│       │   ├── ITransactionFetcher.cs    ← Fetcher interface
│       │   ├── HttpTransactionFetcher.cs ← Real HTTP API client
│       │   ├── MockTransactionFetcher.cs ← Local JSON file reader
│       │   └── IngestionService.cs       ← Core ingestion logic
│       ├── appsettings.json
│       └── Program.cs
│
└── tests/
    └── TransactionsIngest.Tests/
        ├── TestFactory.cs                ← In-memory DB + service builder
        └── IngestionServiceTests.cs      ← 11 xUnit test cases
```

---

## Data Model

### Transactions Table

| Column | Type | Notes |
|---|---|---|
| TransactionId | string (PK) | Stable unique identifier from upstream |
| CardLast4 | string(4) | Last 4 digits only — full PAN is never stored |
| LocationCode | string(20) | Store/location identifier |
| ProductName | string(20) | Name of the purchased product |
| Amount | decimal(18,2) | Transaction amount |
| TransactionTime | DateTime (UTC) | When the transaction occurred |
| Status | string | Active / Revoked / Finalized |
| CreatedAt | DateTime (UTC) | When this record was first inserted |
| UpdatedAt | DateTime (UTC) | When this record was last modified |

### Audits Table (append-only)

| Column | Type | Notes |
|---|---|---|
| Id | int (PK, auto) | Auto-increment |
| TransactionId | string (FK) | References Transactions |
| ChangeType | string | Insert / Update / Revoke / Finalize |
| ChangeDetail | string? | e.g. "Amount: 19.99 -> 24.99; LocationCode: STO-01 -> STO-02" |
| ChangedAt | DateTime (UTC) | When this change occurred |

---

## How It Works

Each run executes these steps inside a **single database transaction**:

1. **Fetch** — load the 24-hour snapshot from the API (or mock feed)
2. **Upsert** — for each incoming transaction:
   - If new → insert and write an `Insert` audit row
   - If exists and changed → update fields and write an `Update` audit row listing every changed field
   - If exists and unchanged → do nothing (idempotent)
3. **Revoke** — any `Active` record within the 24-hour window that is absent from the current snapshot is marked `Revoked`
4. **Finalize** — any `Active` record older than 24 hours is marked `Finalized` and permanently locked
5. **Commit** — all changes saved atomically. If anything fails, the entire run rolls back.

---

## Prerequisites

| Requirement | Version |
|---|---|
| .NET SDK | 10.0 or later |

Download: https://dotnet.microsoft.com/download/dotnet/10.0

Verify:
```bash
dotnet --version
# 10.x.x
```

---

## Build & Run Steps

### 1. Clone the repository
```bash
git clone https://github.com/saisandeep14/TransactionsIngest.git
cd TransactionsIngest
```

### 2. Restore packages
```bash
dotnet restore
```

### 3. Copy the mock feed to the project root
```bash
cp src/TransactionsIngest/mock-feed.json ./mock-feed.json
```

### 4. Generate the database migration
```bash
dotnet tool install --global dotnet-ef

dotnet ef migrations add InitialCreate \
  --project src/TransactionsIngest \
  --startup-project src/TransactionsIngest
```

### 5. Run the application
```bash
dotnet run --project src/TransactionsIngest
```

Expected output (first run):
```
info: Applying database migrations...
info: Reading mock feed from mock-feed.json
info: Loaded 6 transaction(s) from mock feed.
info: Run started at 2026-03-11 17:31:13Z. Window cutoff: 2026-03-10 17:31:13Z. Incoming: 6 record(s).
info: Run complete. Inserted=6, Updated=0, Revoked=0, Finalized=0, AuditRows=6.
info: Ingestion run finished successfully.
```

Run again (idempotency — all zeros):
```
info: Run complete. Inserted=0, Updated=0, Revoked=0, Finalized=0, AuditRows=0.
```

---

## Running the Tests

```bash
dotnet test
```

Expected output:
```
Test summary: total: 11, failed: 0, succeeded: 11, skipped: 0, duration: 0.6s
```

### Test Coverage

| Test | Scenario |
|---|---|
| `NewTransaction_IsInserted_WithAuditRow` | New transaction is inserted with an Insert audit row |
| `ChangedAmount_IsDetected_AndAudited` | Amount change is detected and recorded in audit |
| `ChangedLocation_IsDetected_AndAudited` | Location code change is detected and recorded |
| `IdenticalRun_ProducesNoNewAuditRows` | Repeated run with same data creates zero new audit rows |
| `IdenticalRun_ProducesNoDuplicateTransactionRows` | Repeated run keeps exactly 1 transaction row |
| `ActiveRecord_AbsentInNextSnapshot_IsRevoked` | Missing transaction is marked Revoked |
| `RevokedRecord_ReappearsInSnapshot_IsReActivated` | Re-appearing revoked record becomes Active |
| `ActiveRecord_OlderThanWindow_IsFinalized` | Record older than 24h is marked Finalized |
| `FinalizedRecord_IsNotModifiedBySubsequentRuns` | Finalized records are permanently locked |
| `MultipleChangedFields_AllCapturedInSingleAuditRow` | Multiple changes produce one Update audit row |
| `FullCardNumber_IsNeverPersisted_OnlyLast4Stored` | Full PAN is never saved — only last 4 digits |

---

## Configuration

`src/TransactionsIngest/appsettings.json`:

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

| Setting | Purpose |
|---|---|
| `DefaultConnection` | Path to the SQLite database file |
| `ApiBaseUrl` | Base URL of the real transactions API |
| `ApiPath` | Endpoint path appended to the base URL |
| `MockFeedPath` | If set, reads from this local JSON file instead of calling the API. Remove in production. |
| `SnapshotWindowHours` | How many hours back the snapshot covers (default: 24) |

---

## Simulating Scenarios

**Simulate an update:** Edit `mock-feed.json`, change an `amount`, then run again:
```bash
dotnet run --project src/TransactionsIngest
# → Updated=1, AuditRows=1 with "Amount: X -> Y"
```

**Simulate a revocation:** Delete a transaction from `mock-feed.json`, then run again:
```bash
dotnet run --project src/TransactionsIngest
# → Revoked=1
```

**Reset the database:**
```bash
rm src/TransactionsIngest/transactions.db
dotnet run --project src/TransactionsIngest
```

---

## Design Decisions & Assumptions

- **Privacy:** Full card PANs are never persisted. Only the last 4 digits are extracted at ingest time (`CardLast4`).
- **Idempotency:** The entire run is wrapped in a single SQLite transaction. A repeated run with identical input produces no new rows.
- **Audit trail:** The `Audits` table is append-only. Rows are never updated or deleted — complete immutable history for compliance.
- **Revoked vs. Finalized:** A `Revoked` record is within the 24-hour window but currently absent — it may reappear (network delay) and will be re-activated. A `Finalized` record is older than the window and permanently locked.
- **Last-write-wins:** If the same `TransactionId` appears more than once in a snapshot, the last occurrence wins.
- **Status stored as string:** `TransactionStatus` is stored as `"Active"`, `"Revoked"`, `"Finalized"` — human-readable without a code lookup.
- **Scheduler:** Single-run execution. An external scheduler (cron / Windows Task Scheduler) runs it hourly.
