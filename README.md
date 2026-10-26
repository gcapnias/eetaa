# EETAA Settlement Administrative History Worker

A high-performance, resilient .NET 10 console worker application for crawling, discovering, and ingesting Greek settlement administrative changes published by EETAA (Hellenic Agency for Local Development and Local Government / Ελληνική Εταιρία Τοπικής Ανάπτυξης και Αυτοδιοίκησης) into a local SQLite database.

---

## Overview

The EETAA publishes historical statutory records documenting every administrative change of Greek settlements from the 19th century to modern times. This application automates:

1. Crawling the paginated settlement index to discover settlement IDs and names.
2. Ingesting per-settlement CSV exports containing statutory administrative change history.
3. Normalizing and storing all records in a local SQLite database with transactional integrity and crash resumability.

---

## Domain Concepts & Vocabulary

This project adheres to the official domain terminology defined in [GLOSSARY.md](GLOSSARY.md):

- **Settlement**: An officially recognized Greek populated locality or community identified by a unique EETAA numerical identifier (`id` or `sid`). *(Avoid: Village, town, city, locality, place)*
- **Administrative Change**: An official statutory modification of a settlement's legal status, name, or local government attachment (such as annexation, renaming, merger, or transfer to another municipality/prefecture), enacted via a Government Gazette issue. *(Avoid: Mutation, transition, modification, update)*
- **Government Gazette (FEK)**: The official statutory journal of the Hellenic Republic (Φύλλο Εφημερίδας της Κυβερνήσεως) that legalizes an administrative change, specified by year, issue number, and publication date. *(Avoid: Decree, legislation, bulletin, gazette issue)*
- **Prefecture (Nomos)**: The regional administrative unit of Greece to which a settlement was historically assigned at the time of a specific administrative change. *(Avoid: County, province, region, department)*
- **Settlement Queue**: The persistent local inventory (`settlement_queue` table) that tracks each known settlement identifier, its crawl lifecycle state (`pending`, `processing`, `completed`, `failed`), attempt count, and last fetch timestamp. *(Avoid: Task list, fetch job, work items, download queue)*

---

## Architecture & Pipeline Phases

The worker implements a staged, decoupled architecture designed for high network concurrency and SQLite write safety:

```text
[ EETAA Web Endpoint ]
         │
         ▼  (HTTP GET with polite rate limiting & bounded concurrency)
┌────────────────────────┐
│ IndexDiscoveryCrawler  │──(Inserts discovered settlements)──┐
└────────────────────────┘                                     │
                                                               ▼
┌──────────────────────────────┐                       ┌──────────────────┐
│  SettlementIngestionService  │◄──(Fetch pending)─────│ settlement_queue │
└──────────────────────────────┘                       └──────────────────┘
         │                                                     ▲
         ▼  (Stream CSV & parse changes)                       │
┌──────────────────────────────┐                               │
│  SingleWriterChannel         │                               │
│  (System.Threading.Channels) │                               │
└──────────────────────────────┘                               │
         │                                                     │
         ▼  (Single consumer loop)                             │
┌──────────────────────────────┐                               │
│  SingleWriterStorageEngine   │──(Isolated transactions)──────┘
└──────────────────────────────┘       │
                                       ▼
                             ┌──────────────────┐
                             │ oikmet_details   │
                             │ (SQLite WAL DB)  │
                             └──────────────────┘
```

1. **Index Discovery Phase (`--mode=discover` or `--mode=all`)**:
   - Crawls paginated index pages (`oik_metaboles.php?ltr=ALL&pg=...`) concurrently with polite inter-request delay.
   - Extracts settlement IDs and Greek settlement names, storing them in the `settlement_queue` table with `status = 'pending'`.
   - Supports page range controls (`--start-page`, `--max-pages`).

2. **Concurrent Ingestion & Rate Throttling (`--mode=download` or `--mode=all`)**:
   - Pulls pending settlements from `settlement_queue` and streams CSV exports (`oikmet2csv.php?sid={id}`).
   - Controls concurrency with a bounded semaphore (`--concurrency`) and enforces polite inter-request delays (`--delay-ms`).
   - Protects against transient HTTP 5xx and 429 errors using exponential backoff retry with jitter.
   - Parses Greek UTF-8 semicolon-delimited CSV payloads, normalizing `DD/MM/YYYY` FEK dates to ISO-8601 (`YYYY-MM-DD`) and sanitizing unassigned columns.

3. **Single-Writer SQLite Storage Engine**:
   - Eliminates `SQLITE_BUSY` database lock contention by routing all write operations through a dedicated background `System.Threading.Channels.Channel<SettlementPayload>`.
   - Executes each settlement's ingestion inside an isolated, durable transaction: purges prior change records for that settlement, inserts normalized rows into `oikmet_details`, and updates `settlement_queue` status to `completed`.
   - Tunes SQLite for concurrent reading and high write throughput using `PRAGMA journal_mode=WAL;` and `PRAGMA synchronous=NORMAL;`.
   - Automatically recovers interrupted in-flight settlements on startup by resetting orphaned `processing` records back to `pending` in the Settlement Queue.

---

## Prerequisites

- **.NET 10 SDK** (v10.0 or later)
- **SQLite** (bundled runtime via `Microsoft.Data.Sqlite`)

---

## Building and Testing

### Build the Solution

```bash
dotnet build
```

### Run All Unit and Integration Tests

```bash
dotnet test
```

The test suite covers CLI option parsing, HTML index parsing, CSV parsing, date normalization, retry policies, repository transactions, and end-to-end worker lifecycle flows.

---

## CLI Options Reference

The worker CLI options are parsed by `WorkerCliOptions`. Arguments can be supplied in either `--option <value>` or `--option=<value>` syntax.

| Option | Type | Default | Description |
| --- | --- | --- | --- |
| `--mode` | string | `all` | Execution mode: `all` (discover and download), `discover` (index crawl only), or `download` (CSV ingestion only). |
| `--db` | string | `data/eetaa.db` | Relative or absolute file path to the target SQLite database. |
| `--base-url` | string | `https://eetaa.gr/eetaa/metaboles/oik_metaboles.php` | Base URL of the EETAA settlement index endpoint. |
| `--concurrency` | integer | `3` | Maximum number of concurrent HTTP requests for crawling and downloading. |
| `--delay-ms` | integer | `150` | Polite inter-request delay in milliseconds between HTTP requests. |
| `--start-page` | integer | `1` | Starting page number for the index discovery phase. |
| `--max-pages` | integer | *(none / uncapped)* | Maximum number of index pages to crawl during discovery. When omitted, crawls all available pages. |
| `--retry-failed` | flag (switch) | `false` | When present, resets `failed` Settlement Queue items back to `pending` before running ingestion. |
| `--force` | flag (switch) | `false` | When present, forces re-download and re-ingestion of already `completed` settlements. |

---

## Usage Examples

All examples assume execution from the repository root via `dotnet run --project src/Eetaa.Worker/`.

### 1. Full Pipeline (Default)

Run both discovery and ingestion using default settings (`data/eetaa.db`, concurrency 3, delay 150ms):

```bash
dotnet run --project src/Eetaa.Worker/
# Or explicitly:
dotnet run --project src/Eetaa.Worker/ -- --mode all
```

### 2. Discovery Only

Crawl the settlement index starting from page 1 up to page 10 and record discovered settlements in the queue:

```bash
dotnet run --project src/Eetaa.Worker/ -- --mode discover --start-page 1 --max-pages 10
```

### 3. Ingestion (Download) Only

Process existing pending settlements from the queue without re-crawling index pages:

```bash
dotnet run --project src/Eetaa.Worker/ -- --mode download
```

### 4. Custom Database File Path

Specify a custom database destination (the directory and database schema are created automatically):

```bash
dotnet run --project src/Eetaa.Worker/ -- --db "data/custom_eetaa.db"
```

### 5. Conservative / Polite Rate Throttling

Throttle requests gently when running against sensitive networks or rate limits (single connection, 500ms inter-request delay):

```bash
dotnet run --project src/Eetaa.Worker/ -- --concurrency 1 --delay-ms 500
```

### 6. Retry Failed Ingestions

Re-queue previously failed settlements (e.g. after network drops or server timeouts) and resume ingestion:

```bash
dotnet run --project src/Eetaa.Worker/ -- --mode download --retry-failed
```

### 7. Force Full Re-Ingestion

Force re-download and re-parsing of all settlements, including already completed entries:

```bash
dotnet run --project src/Eetaa.Worker/ -- --mode download --force
```

---

## Database Schema Summary

The SQLite database initialized at the configured `--db` path contains two primary tables:

- **`settlement_queue`**: Tracks crawl lifecycle state per settlement.
  - `settlement_id` (INTEGER PRIMARY KEY)
  - `settlement_name` (TEXT)
  - `status` (TEXT: `pending`, `processing`, `completed`, `failed`)
  - `attempts` (INTEGER)
  - `last_attempt_at` (TEXT ISO-8601)
  - `created_at` (TEXT ISO-8601)
  - `updated_at` (TEXT ISO-8601)

- **`oikmet_details`**: Stores normalized administrative changes per settlement.
  - `settlement_id` (INTEGER)
  - `change_order` (INTEGER)
  - `fek_issue` (TEXT)
  - `fek_date` (TEXT ISO-8601 `YYYY-MM-DD` or fallback raw string)
  - `fek_date_raw` (TEXT)
  - `change_description` (TEXT)
  - `nomos` (TEXT)
  - `created_at` (TEXT ISO-8601)
  - Composite primary key: `(settlement_id, change_order)`
