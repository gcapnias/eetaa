# Local Authority Ingestion and Multi-Entity Pipeline Architecture

Context: We are expanding the EETAA worker to harvest Greek Municipalities and Communities (Δήμοι και Κοινότητες / Local Authorities) administrative changes alongside Settlements.

Decision:

1. Entity Isolation and Storage Schema:
   - Introduce dedicated tables in SQLite: `local_authority_queue` (tracking crawl state and retries) and `dkmet_details` (storing normalized administrative changes).
   - In domain code, represent the statutory status as `AuthorityType` (coded 'Δ' for Municipality, 'Κ' for Community), mapped to SQLite column `ota_type` and CSV header `"ΕΙΔΟΣ ΟΤΑ"`.
   - SQLite enforces `PRAGMA foreign_keys = ON;` alongside WAL and NORMAL synchronous modes.
   - Indices on `local_authority_queue (status)` and `dkmet_details (authority_id, fek_date)` ensure rapid lookups and queryability.
   - Composite uniqueness on `(authority_id, change_order)` guarantees idempotent ingestion per local authority.
2. Direct CSV Endpoint Targeting & Separate Configuration:
   - Index discovery crawls `dk_metaboles.php?ltr=ALL&pg=1..937` to harvest authority IDs and name hints.
   - CSV ingestion targets `dkmet2csv.php?sid={id}` directly, bypassing intermediate HTML details pages to eliminate thousands of redundant HTTP requests.
   - Endpoint URLs are configured distinctly per entity rather than overloaded into a single base URL.
3. Multi-Entity Pipeline Orchestration:
   - Introduce an `--entity` CLI flag (`all`, `settlements`, `authorities`, default `all`) composable with `--mode` (`all`, `discover`, `download`).
   - Default execution (`--entity=all --mode=all`) executes sequentially per entity: Settlement Discovery -> Settlement Ingestion -> Local Authority Discovery -> Local Authority Ingestion, ensuring transactional isolation and clean resumability.
   - On startup, in-flight recovery resets orphaned `processing` records to `pending` across both queues.
4. Concurrency and Scoped Single-Writer Discipline:
   - Each entity ingestion phase operates its own dedicated single-writer channel and storage consumer lifecycle, preventing `SQLITE_BUSY` lock contention while keeping drain timeouts and channel completion strictly deterministic.
