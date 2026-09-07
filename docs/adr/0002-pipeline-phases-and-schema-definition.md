# Architecture of EETAA Ingestion Pipeline and Storage Schema

Context: We are building a .NET 10 console worker application to harvest Greek settlement administrative changes from EETAA and store them in SQLite.

Decision:

1. We establish a two-phase architecture:
   - Index discovery phase: crawls pagination (`oik_metaboles.php?ltr=ALL&pg=1..2247`) and populates `settlement_queue`.
   - CSV ingestion phase: downloads each settlement's CSV (`oikmet2csv.php?sid={id}`) and inserts normalized administrative changes into `oikmet_details`.
   - Execution modes can be targeted via CLI flags (`--mode=discover`, `--mode=download`, or `--mode=all` by default).
2. Direct ADO.NET (`Microsoft.Data.Sqlite`) manages all database access and migrations inside a local SQLite database at `data/eetaa.db`.
3. In `oikmet_details`, dates in `DD/MM/YYYY` format are normalized to ISO-8601 (`YYYY-MM-DD`) for SQLite queryability, with original text as fallback if unparseable.
4. Composite uniqueness on `(settlement_id, change_order)` guarantees idempotent ingestion per settlement while preserving historical chronological sequence.
