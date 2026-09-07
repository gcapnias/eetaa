# Ingestion Mechanics, Concurrency, and Transaction Boundaries

Context: Ingesting thousands of Greek settlement administrative changes over HTTP requires resilient error handling, bounded concurrency, streaming CSV parsing, and fast transactional writes into SQLite.

Decision:

1. Concurrency and Throttling:
   - Index crawling (`oik_metaboles.php?ltr=ALL&pg={page}`) runs with bounded concurrency (default 3 concurrent HTTP requests) and polite inter-request delay (100-250ms).
   - CSV downloading (`oikmet2csv.php?sid={sid}`) processes the `settlement_queue` concurrently using `SemaphoreSlim` or `Parallel.ForEachAsync` with bounded worker count.
2. CSV Parsing:
   - Use `CsvHelper` with Greek UTF-8 BOM awareness, semicolon (`;`) delimiter, and header validation.
   - Blank or unassigned FEK columns are sanitized to `NULL`.
   - Dates in `DD/MM/YYYY` format are normalized to ISO-8601 strings (`YYYY-MM-DD`).
3. Transaction Boundaries & SQLite Performance:
   - SQLite is initialized with `PRAGMA journal_mode=WAL;` and `PRAGMA synchronous=NORMAL;`.
   - Each settlement's CSV ingestion executes within a dedicated, isolated transaction (deleting previous change entries for that settlement and inserting the new set, followed by updating `settlement_queue` status to `completed`). This ensures instant commit durability and clean resume semantics if execution is interrupted.
4. CLI Control and Filters:
   - Default mode is `--mode=all` (discovers new settlements, then downloads pending CSVs).
   - Modes `--mode=discover` and `--mode=download` are available for targeted execution.
   - Flags `--force` and `--retry-failed` allow re-evaluating completed or failed queue items.
