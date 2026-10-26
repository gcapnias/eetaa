# SQLite Concurrency Safety and Data Ingestion Guardrails

Context: The original settlement architecture required guardrails for SQLite concurrency, Greek FEK date variance, and fault tolerance. These guardrails also apply to local authorities under [ADR-0005](0005-local-authority-ingestion-and-multi-entity-pipeline.md).

Decision:

1. SQLite Single-Writer Pattern:
   - Concurrent HTTP downloaders publish detail payloads to a dedicated bounded channel (`System.Threading.Channels.Channel<T>`). One consumer per active stage serializes detail persistence and its atomic queue completion/failure updates. Discovery inserts and queue `processing` updates also write outside this consumer.
   - This reduces concurrent detail-write contention; it does not guarantee that unrelated SQLite writers can never cause lock contention.
2. In-Flight Recovery on Startup:
   - Any records left in `processing` status due to unexpected shutdown or power loss are reset to `pending` on application start:
     `UPDATE settlement_queue SET status = 'pending' WHERE status = 'processing';`
     `UPDATE local_authority_queue SET status = 'pending' WHERE status = 'processing';`
   - Both queues are recovered at startup regardless of the selected entity. Ingestion also performs recovery for its own queue before selecting items.
3. Resilient Date and Payload Parsing:
   - FEK dates use exact day/month/year formats and a calendar-validating fallback. Unparseable dates have a `NULL` normalized date and retain trimmed source text in `fek_date_raw`; blank input gives `NULL` in both fields.
   - Header-only CSVs are valid empty payloads and mark the queue item `completed`, with zero change rows in the relevant detail table.
4. HTTP Resilience:
   - CSV downloads retry HTTP 429, 500, 502, 503, and 504, connection errors, and timeouts with exponential backoff and jitter, up to three retries after the initial attempt. Worker cancellation stops retries. Clients send an explicit User-Agent. Discovery requests do not use the CSV retry policy.
5. Consumer Lifetime and Failure Coordination:
   - No drain deadline runs during normal ingestion. The worker arms a 30-second drain timeout when ingestion ends or host cancellation is requested, then completes the channel after the producer ends and waits for pending writes. The host shutdown lifetime also applies.
   - If the storage consumer exits prematurely, the worker cancels the producer and completes the channel to unblock waiting producers. Uncommitted items can remain `processing` and are eligible for startup recovery.
