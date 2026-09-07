# SQLite Concurrency Safety and Data Ingestion Guardrails

Context: An independent advisory evaluation reviewed the proposed EETAA settlement scraping architecture and identified critical operational guardrails for SQLite concurrency, Greek FEK date variance, and fault tolerance.

Decision:

1. SQLite Single-Writer Pattern:
   - While HTTP fetching runs concurrently across multiple workers, all SQLite write transactions are routed through a dedicated single-writer channel (`System.Threading.Channels.Channel<T>`).
   - This eliminates `SQLITE_BUSY` database lock contention while maintaining high network concurrency and per-settlement transaction durability.
2. In-Flight Recovery on Startup:
   - Any records left in `processing` status due to unexpected shutdown or power loss are reset to `pending` on application start:
     `UPDATE settlement_queue SET status = 'pending' WHERE status = 'processing';`
3. Resilient Date and Payload Parsing:
   - FEK dates are parsed using multi-pass format checking (`dd/MM/yyyy`, `d/M/yyyy`), falling back to raw string or `NULL` on irregular historical entries (e.g. `//1928` or missing dates) to prevent failing valid settlements.
   - Header-only CSV responses (0 rows) are recognized as valid empty datasets and marked `completed` (with 0 entries in `oikmet_details`), rather than throwing errors.
4. HTTP Resilience:
   - HTTP requests include structured retries with exponential backoff and jitter on transient 5xx/429 errors, with an explicit User-Agent.
