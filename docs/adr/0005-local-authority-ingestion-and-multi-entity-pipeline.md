# Local Authority Ingestion and Multi-Entity Pipeline Architecture

Context: The EETAA worker harvests Greek Municipalities and Communities (Δήμοι και Κοινότητες / Local Authorities) administrative changes alongside Settlements. This decision extends the settlement-only scope of ADRs 0001–0004; their current descriptions incorporate this extension.

Decision:

1. Entity Isolation and Storage Schema:
   - Introduce dedicated tables in SQLite: `local_authority_queue` (tracking crawl state and retries) and `dkmet_details` (storing normalized administrative changes).
   - In domain code, represent the statutory status as `AuthorityType` (coded 'Δ' for Municipality, 'Κ' for Community), mapped to SQLite column `ota_type` and CSV header `"ΕΙΔΟΣ ΟΤΑ"`.
   - SQLite enforces `PRAGMA foreign_keys = ON;` alongside WAL and NORMAL synchronous modes.
   - Index `local_authority_queue(status)` and create separate indexes on `dkmet_details(authority_id)` and `dkmet_details(fek_date)`.
   - Composite uniqueness on `(authority_id, change_order)` guarantees idempotent ingestion per local authority.
2. Direct CSV Endpoint Targeting & Separate Configuration:
   - Index discovery crawls `dk_metaboles.php?ltr=ALL&pg={page}` to harvest authority IDs and name hints. Page counts are detected from HTML; 937 was an observed count, not a hardcoded bound. Concurrent page results are persisted in ascending page order to preserve first-seen name hints.
   - CSV ingestion targets `dkmet2csv.php?sid={id}` directly, bypassing intermediate HTML details pages to eliminate thousands of redundant HTTP requests.
   - Discovery URLs are distinct CLI options: `--settlements-base-url` and `--authorities-base-url`. The legacy `--base-url` only affects settlement discovery while its effective URL still equals the built-in default. CSV endpoints are currently fixed in `Program.cs`.
   - `appsettings.json` has an `Eetaa` section bound to `EetaaOptions`, but those bound values are not consumed by the runtime crawlers or ingestion services. This configuration surface does not currently override endpoints. See [README.md](../../README.md#cli-options) for effective options and defaults.
3. Multi-Entity Pipeline Orchestration:
   - Introduce an `--entity` CLI flag (`all`, `settlements`, `authorities`, default `all`) composable with `--mode` (`all`, `discover`, `download`).
   - Default execution (`--entity=all --mode=all`) executes sequentially per entity: Settlement Discovery -> Settlement Ingestion -> Local Authority Discovery -> Local Authority Ingestion, ensuring transactional isolation and clean resumability.
   - On startup, in-flight recovery resets orphaned `processing` records to `pending` across both queues.
4. Concurrency and Scoped Single-Writer Discipline:
   - Each entity ingestion phase has its own scoped channel and consumer lifecycle, with a separate progress coordinator. Phases execute sequentially, preventing a completed channel from being reused by the next entity. The bounded channels serialize detail persistence; queue and discovery writes also occur outside the consumers.
   - The 30-second drain timeout applies after ingestion ends or shutdown is requested, not throughout active ingestion. Premature consumer exit cancels the producer and completes the channel. See [ADR-0004](0004-sqlite-concurrency-and-ingestion-guardrails.md).
