# Storage Architecture and Direct ADO.NET Data Access

Context: We need to ingest ~33,000+ administrative changes across thousands of settlements from EETAA into a local SQLite database with resumability, transactional integrity, and minimal memory overhead.

Decision: We will use SQLite via pure ADO.NET (`Microsoft.Data.Sqlite`) without any ORM or micro-ORM (no Entity Framework, no Dapper). State management will be divided cleanly into a queue table (`settlement_queue`) tracking crawl lifecycle and retry counters, and the primary data table (`oikmet_details`) storing normalized change events parsed directly from EETAA CSV responses.

Why:

1. Zero abstraction overhead and exact control over connection lifetimes, SQLite PRAGMA tuning (WAL mode, synchronous=NORMAL), and batch transactions.
2. Complete transparency and predictability in schema definitions and index usage for the target tables.
3. Easy multi-stage resumption without ORM change-tracking complexities.
