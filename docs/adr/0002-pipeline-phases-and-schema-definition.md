# Architecture of EETAA Ingestion Pipeline and Storage Schema

Context: The .NET 10 worker harvests settlement and local authority administrative changes from EETAA into SQLite. [ADR-0005](0005-local-authority-ingestion-and-multi-entity-pipeline.md) extends the original settlement pipeline to both entities.

Decision:

1. Each entity has discovery and CSV ingestion phases:
   - Settlements use `oik_metaboles.php?ltr=ALL&pg={page}`, `settlement_queue`, `oikmet2csv.php?sid={id}`, and `oikmet_details`.
   - Local authorities use `dk_metaboles.php?ltr=ALL&pg={page}`, `local_authority_queue`, `dkmet2csv.php?sid={id}`, and `dkmet_details`.
   - Page counts are detected from HTML rather than fixed at historical counts. `--start-page` and `--max-pages` bound discovery for each selected entity.
   - `--entity=all|settlements|authorities` composes with `--mode=all|discover|download`; both default to `all`. The default stage order is settlement discovery, settlement ingestion, local authority discovery, then local authority ingestion.
2. Direct ADO.NET manages schema initialization and data access. The default database is `data/eetaa.db`, relative to the process working directory; `--db` overrides it.
3. Both change tables store valid FEK dates in `fek_date` as ISO `YYYY-MM-DD`. Unparseable dates have `fek_date = NULL`; trimmed original text is retained in `fek_date_raw`, including placeholders and partial historical dates.
4. Composite keys are `(settlement_id, change_order)` and `(authority_id, change_order)`, with cascading foreign keys to the corresponding queues. Change Order preserves parsed CSV row order, not a sort by date. Successful ingestion replaces all change rows for an entity in one transaction.
