# Storage Architecture and Direct ADO.NET Data Access

Context: We need to ingest settlement and local authority administrative changes from EETAA into a local SQLite database with resumability, transactional integrity, and minimal memory overhead. Local authority support extends the original settlement scope as recorded in [ADR-0005](0005-local-authority-ingestion-and-multi-entity-pipeline.md).

Decision: Use SQLite via direct ADO.NET (`Microsoft.Data.Sqlite`) without an ORM or micro-ORM. Each entity has a queue tracking ingestion status and recorded attempts, and a separate administrative change table: `settlement_queue` / `oikmet_details` and `local_authority_queue` / `dkmet_details`. Table creation uses `CREATE TABLE IF NOT EXISTS`; the implementation does not provide a versioned migration system. See [README.md](../../README.md#database-schema) for current columns and keys.

Why:

1. Zero abstraction overhead and exact control over connection lifetimes, SQLite PRAGMA tuning (WAL mode, synchronous=NORMAL), and batch transactions.
2. Complete transparency and predictability in schema definitions and index usage for the target tables.
3. Easy multi-stage resumption without ORM change-tracking complexities.
