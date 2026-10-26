# EETAA Administrative History Worker

A .NET 10 console worker that discovers and ingests Greek settlement and local authority administrative changes published by EETAA into a local SQLite database.

## Domain and architecture

The worker handles **Settlements** and **Local Authorities** (Municipalities / Δήμοι and Communities / Κοινότητες). Each has its own queue and administrative change records. See [GLOSSARY.md](GLOSSARY.md) for terminology and [the ADRs](docs/adr/) for architecture decisions.

| Entity | Discovery endpoint | CSV endpoint | Queue | Administrative changes |
| --- | --- | --- | --- | --- |
| Settlement | `oik_metaboles.php` | `oikmet2csv.php?sid={id}` | `settlement_queue` | `oikmet_details` |
| Local Authority | `dk_metaboles.php` | `dkmet2csv.php?sid={id}` | `local_authority_queue` | `dkmet_details` |

Default endpoints are under `https://eetaa.gr/eetaa/metaboles/`. Discovery detects pagination from HTML and inserts previously unseen identifiers into the relevant queue. Local authority discovery preserves the first name hint in page order when an identifier appears more than once.

With defaults (`--entity=all --mode=all`), stages run sequentially:

```text
Settlement Discovery -> Settlement Ingestion
    -> Local Authority Discovery -> Local Authority Ingestion
```

`--entity` selects entities; `--mode` selects discovery, download, or both for those entities. Startup creates both sets of tables and resets orphaned `processing` records in both queues, even when only one entity is selected.

During ingestion, concurrent HTTP downloads parse CSV payloads and feed a bounded channel (capacity 1,000). One storage consumer saves each payload in a transaction that replaces the entity's changes and marks its queue item `completed`. Each ingestion stage has its own channel, consumer, and progress coordinator. Discovery and queue status updates also write to SQLite outside this consumer.

CSV parsing handles Greek UTF-8 with or without a BOM, semicolon delimiters, and quoted fields. Settlement exports have seven columns; local authority exports have eight, including Authority Type. Parsers match Greek headers with positional fallbacks. Valid FEK dates become ISO `YYYY-MM-DD`; unparseable dates have `fek_date = NULL` and retain trimmed source text in `fek_date_raw`. Header-only files complete with zero change records.

## Prerequisites, build, and tests

- .NET 10 SDK.
- SQLite is supplied by `Microsoft.Data.Sqlite`; no separate database server is required.

Run from the repository root:

```sh
dotnet restore
dotnet build --no-restore
dotnet test --no-restore
```

Tests use recorded fixtures, simulated HTTP responses, and temporary SQLite databases. They cover parsing, repositories, retries, progress output, multi-entity orchestration, crash recovery, storage consumer failure, and graceful drain.

## CLI options

Value options accept `--option value` and `--option=value`. Boolean options are presence-only switches.

| Option | Type | Default | Behavior |
| --- | --- | --- | --- |
| `--entity` | string | `all` | `all`, `settlements`, or `authorities`. |
| `--mode` | string | `all` | `all` runs discovery then ingestion per selected entity; `discover` only discovers; `download` only ingests existing queue items. |
| `--db` | string | `data/eetaa.db` | SQLite path relative to the process working directory, or absolute. Parent directories are created as needed. |
| `--base-url` | string | `https://eetaa.gr/eetaa/metaboles/oik_metaboles.php` | Legacy settlement discovery URL; does not change authority discovery or CSV endpoints. |
| `--settlements-base-url` | string | `https://eetaa.gr/eetaa/metaboles/oik_metaboles.php` | Settlement discovery URL. |
| `--authorities-base-url` | string | `https://eetaa.gr/eetaa/metaboles/dk_metaboles.php` | Local authority discovery URL. |
| `--concurrency` | integer | `3` | Maximum concurrent requests within a stage; processing uses at least one worker. |
| `--delay-ms` | integer | `150` | Delay per worker between requests; nonpositive values disable it. |
| `--start-page` | integer | `1` | First discovery page, clamped to at least 1; applies to each selected entity. |
| `--max-pages` | integer | unset | Discovery page-count cap from the start page, bounded by detected pagination. Use a positive value. |
| `--retry-failed` | switch | absent | Select `pending` and `failed` queue items for ingestion. |
| `--force` | switch | absent | Select every existing queue item, including completed and failed items. Takes precedence over `--retry-failed`. |

Use `--settlements-base-url` for new commands. When both settlement URL options are supplied, an explicit nondefault settlement URL takes precedence over the legacy URL. Internally, `--base-url` only assigns the effective settlement URL while it still equals the built-in default; a later `--settlements-base-url` always assigns it.

Runtime options come from `WorkerCliOptions.Parse` and its defaults. The `Eetaa` endpoint section in `appsettings.json` is bound to `EetaaOptions`, but running crawlers and ingestion services do not consume those bound values. Discovery URLs use CLI options; CSV URLs are currently fixed in `Program.cs`. Editing that JSON section does not redirect HTTP requests.

## Usage examples

Run from the repository root. Select `--entity=settlements` to retain a settlement-only workflow.

Full four-stage pipeline for both entities:

```sh
dotnet run --project src/Eetaa.Worker/ -- --entity=all --mode=all
```

Discover up to ten settlement index pages without downloading CSVs:

```sh
dotnet run --project src/Eetaa.Worker/ -- --entity=settlements --mode=discover --start-page=1 --max-pages=10
```

Discover and ingest only local authorities:

```sh
dotnet run --project src/Eetaa.Worker/ -- --entity=authorities --mode=all
```

Resume pending downloads for both entities without repeating discovery:

```sh
dotnet run --project src/Eetaa.Worker/ -- --entity=all --mode=download
```

Include failed local authorities in the next ingestion run:

```sh
dotnet run --project src/Eetaa.Worker/ -- --entity=authorities --mode=download --retry-failed
```

Re-ingest every settlement already in the queue, including completed items:

```sh
dotnet run --project src/Eetaa.Worker/ -- --entity=settlements --mode=download --force
```

Use a custom database and lower concurrency for both entities:

```sh
dotnet run --project src/Eetaa.Worker/ -- --db=data/custom_eetaa.db --concurrency=1 --delay-ms=500
```

## Progress, recovery, and shutdown

Queue states are `pending`, `processing`, `completed`, and `failed`. Normal ingestion selects pending items; retry and force switches widen that selection. Download/parse failures are sent to the storage consumer as failure payloads, which mark the queue item failed and save its error message. A downloaded item is only complete after its SQLite transaction commits.

Transient HTTP failures (429, 500, 502, 503, 504, connection errors, and timeouts) use exponential backoff with jitter, with up to three retries after the initial attempt. Worker cancellation stops retries.

Information-level progress logs report persisted items, total items, percentage, throughput, ETA, failure count, and channel buffer depth. Reports occur every 100 persisted/failed items or every 15 seconds while work remains. A final summary is emitted when needed, including after a partial run.

The storage consumer has no 30-second deadline during active ingestion. When ingestion ends or shutdown is requested, the worker completes the channel and allows up to 30 seconds for drain. If the consumer exits prematurely, the worker cancels the producer and completes the channel to unblock pending writes. Uncommitted items left `processing` can be recovered on the next startup. The host's own shutdown lifetime also applies.

## Database schema

Tables are created with `CREATE TABLE IF NOT EXISTS`; this is initialization, not a versioned migration system. Initialization requests WAL journal mode and NORMAL synchronous mode; connections enable foreign keys. All four tables are initialized regardless of entity selection.

| Queue table | Identifier primary key | Other columns |
| --- | --- | --- |
| `settlement_queue` | `settlement_id` | `name_hint`, `status`, `attempt_count`, `last_attempt_at`, `completed_at`, `error_message`, `created_at`, `updated_at` |
| `local_authority_queue` | `authority_id` | `name_hint`, `status`, `attempt_count`, `last_attempt_at`, `completed_at`, `error_message`, `created_at`, `updated_at` |

Both queues index `status`. A name hint may differ from names in historical change records. `attempt_count` counts recorded ingestion outcomes, not individual HTTP retry attempts.

| Change table | Composite primary key | Entity-specific columns |
| --- | --- | --- |
| `oikmet_details` | (`settlement_id`, `change_order`) | `settlement_name` |
| `dkmet_details` | (`authority_id`, `change_order`) | `ota_type`, `authority_name` |

Both change tables also contain `nomos`, `change_year`, `fek_number`, `fek_issue`, `fek_date`, `fek_date_raw`, `change_description`, `raw_record`, and `created_at`. `ota_type` stores Authority Type as `Δ` or `Κ` when recognized. Each change table references its queue identifier with `ON DELETE CASCADE`, and has separate indexes on its identifier and `fek_date`.

`change_order` preserves parsed CSV row order; records are not sorted by FEK date. Successful re-ingestion atomically replaces an entity's change rows, including removing old rows when the new CSV has no changes. A recorded download/parse failure preserves existing change rows.
