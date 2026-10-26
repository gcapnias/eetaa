# Ingestion Mechanics, Concurrency, and Transaction Boundaries

Context: Ingesting settlement and local authority administrative changes over HTTP requires resilient error handling, bounded concurrency, CSV parsing, and transactional writes into SQLite. The same mechanics apply to both entities under [ADR-0005](0005-local-authority-ingestion-and-multi-entity-pipeline.md).

Decision:

1. Concurrency and Throttling:
   - Index crawling uses bounded concurrency; CSV downloading uses `SemaphoreSlim`. Defaults are 3 workers and a 150ms delay per worker between requests, configurable through `--concurrency` and `--delay-ms`.
   - Each ingestion stage uses its own bounded channel (capacity 1,000) and one storage consumer. A full channel waits for capacity. Entity stages execute sequentially.
2. CSV Parsing:
   - Use `CsvHelper` with Greek UTF-8 BOM awareness and semicolon (`;`) delimiters. Settlement CSVs have seven columns and local authority CSVs have eight. Greek headers are matched with positional fallbacks; strict header validation, missing-field callbacks, and bad-data callbacks are disabled.
   - Blank or unassigned FEK columns are sanitized to `NULL`.
   - Valid day/month/year dates are normalized to ISO `YYYY-MM-DD`; raw date text is preserved separately, and unparseable normalized dates are `NULL`.
3. Transaction Boundaries & SQLite Performance:
   - SQLite is initialized with `PRAGMA journal_mode=WAL;` and `PRAGMA synchronous=NORMAL;`.
   - Each entity payload executes within one transaction: delete that entity's old changes, insert the new set, and mark the corresponding queue item `completed`. An empty successful payload removes previous changes and still completes the queue item. A download/parse failure payload records `failed` and an error message while retaining existing change rows.
   - The transaction provides atomic replacement. NORMAL synchronous mode is not a promise that every committed transaction survives power loss.
4. CLI Control and Filters:
   - Default selection is `--entity=all --mode=all`, running discovery and ingestion for both entities in the order specified in ADR-0005.
   - Modes `--mode=discover` and `--mode=download` are available for targeted execution.
   - Normal ingestion selects `pending` items. `--retry-failed` includes `failed` items. `--force` selects all existing queue items and takes precedence over retry selection. These switches do not themselves repeat discovery.
5. Progress Reporting:
   - Each stage reports persisted count, total, percentage, throughput, ETA, failure count, and channel depth at Information level, every 100 persisted/failed items or every 15 seconds while work remains. Persistence progress follows transaction completion; network download completion is tracked separately.
