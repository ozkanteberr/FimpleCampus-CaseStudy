# Atomic Work Items

The case study was delivered in reviewable increments. Each item below was completed and verified before the next one began. See `VERIFICATION.md` for the final clean-stack run and `README.md` for requirement-level evidence.

| ID | Work item | Acceptance criterion | Result |
| --- | --- | --- | --- |
| G01 | Scope and roadmap | Cover Parts A and B, technical requirements, and deliverables | Verified |
| G02 | .NET solution and dependencies | Release build without errors or warnings | Verified |
| G03 | Banknote selector | Eight required B3 cases and an independent stock oracle pass | Verified |
| G04 | Cassette and lifecycle rules | Currency, denomination, configuration, and state tests pass | Verified |
| G05 | EF model and initial migration | Fresh migration and exchange-rate seed on PostgreSQL | Verified |
| G06 | ATM setup flow | Real-database create, configure, load, and service HTTP flow | Verified |
| G07 | Maintenance and reconfiguration | Invalid states rejected; cash and movement totals preserved | Verified |
| G08 | Immutable movement ledger | SQL updates/deletes rejected; consistency endpoint detects differences | Verified |
| G09 | Container and API surface | Compose startup, Swagger/OpenAPI, ProblemDetails, and health | Verified |
| G10 | Quote and reservation model | Migration and reservation invariants pass | Verified |
| G11 | Card identity and daily limits | HMAC fingerprint, UTC day, and network-wide limits | Verified |
| G12 | Quote endpoint | Exact/lower/upper alternatives and merged reservations | Verified |
| G13 | Duplicate-denomination cassettes | Fuller cassette dispenses first without overbooking | Verified |
| G14 | Quote confirmation | Selected notes debited; unused reservations released | Verified |
| G15 | Confirmation idempotency | Same-key replay is stable; changed request conflicts | Verified |
| G16 | Quote cancellation | Note and daily-limit reservations released | Verified |
| G17 | Automatic quote expiry | Reservations released after 60 seconds; late confirmation rejected | Verified |
| G18 | Maintenance with active quotes | Entering maintenance cancels and releases active quotes | Verified |
| G19 | Concurrent ATM quotes | Parallel requests cannot reserve a physical note twice | Verified |
| G20 | Concurrent card limits | Parallel requests at different ATMs share one card limit | Verified |
| G21 | Deposit routing | Recycling, then deposit-only, then return; unrecognized notes excluded | Verified |
| G22 | Deposit boundaries | Mixed notes, duplicate cassettes, capacity, and partial returns | Verified |
| G23 | Reconciliation calculation | Every cassette counted; differences and rate snapshots recorded | Verified |
| G24 | Second-user approval | Difference above TRY 1,000 blocks service until another user approves | Verified |
| G25 | Reconciliation boundaries | TRY 999/1,000/1,001 and positive/negative differences | Verified |
| G26 | ATM and network reports | Physical, reserved, available, currency, and TRY totals | Verified |
| G27 | Low-cash outbox event | Crossing from 10 to 9 available notes writes an event atomically | Verified |
| G28 | Outbox worker | Pending events logged, marked sent, and retried after failure | Verified |
| G29 | Postman collection and environment | Ordered flow passes IDs through setup, cash operations, approval, and reports | Verified: 22 requests |
| G30 | Error and API contract review | Invalid JSON, null entries, missing headers, and state conflicts use ProblemDetails | Verified: 56 Release tests |
| G31 | Fresh Docker and full verification | New Compose volume, all Release tests, and Postman end-to-end flow | Verified: 56 tests and 22 requests |
| G32 | Documentation and repository handoff | English README, assumptions, limitations, AI note, and A/B traceability | Verified in repository; Git push is performed by the repository owner |

The repository owner chose a Git repository link as the deliverable; no ZIP is required.
