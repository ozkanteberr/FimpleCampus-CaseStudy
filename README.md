# ATM Cash Management Backend

ASP.NET Core 10 REST API for a network of ATMs, backed by PostgreSQL and EF Core code-first migrations. The API covers the ATM lifecycle, bounded note selection, two-step withdrawals, deposits, movement accounting, and physical reconciliation. The remaining delivery work is stated below.

## How to run the project and its tests

Install Docker Desktop with Linux containers and Docker Compose. From the repository root:

```sh
docker compose up -d --build
docker compose ps
```

Compose starts PostgreSQL 17, waits for its health check, and then starts the API. EF Core migrations are applied automatically before requests are served. The API is at `http://localhost:5080`; Swagger UI is at `http://localhost:5080/swagger`, OpenAPI JSON at `http://localhost:5080/openapi/v1.json`, and the database-backed health check at `http://localhost:5080/health`. PostgreSQL is exposed at `127.0.0.1:55432`. Use `docker compose logs --tail 100 api db` to inspect startup failures and `docker compose down` to stop. The `cash_data` volume retains database contents.

For local API development, install the .NET 10 SDK, start only the database with `docker compose up -d db`, then run `dotnet run --project src/AtmCash.Api --urls http://localhost:5080`. The default connection string is in `src/AtmCash.Api/appsettings.json`; override it with `ConnectionStrings__CashDb`. Provide a private `CardIdentity__HmacKey` outside local development.

With PostgreSQL running, execute `dotnet test AtmCashManagement.sln -c Release`. Integration tests create and drop isolated real PostgreSQL databases. Their administrative connection defaults to `Host=127.0.0.1;Port=55432;Database=postgres;Username=atm_app;Password=atm_local_password`; override it with `ATM_TEST_PG`. The latest Release run passed 54/54 tests, including all eight required B3 note-selection cases, concurrency, real-database HTTP flows, deposits, ledger checks, and reconciliation boundaries. For unit tests alone, use `dotnet test AtmCashManagement.sln -c Release --filter 'Category!=Integration'`.

An operational flow is:

1. `POST /api/atms`; configure five cassettes through `PUT /api/atms/{id}/configuration`; record hand-loaded cash through `POST /api/atms/{id}/load` with `X-User`; then `POST /api/atms/{id}/state` with `{"state":"IN_SERVICE"}`.
2. `POST /api/atms/{id}/quotes` with `cardNumber`, `currency`, and `amount`. Select an alternative and `POST /api/quotes/{quoteId}/confirm` with its `alternativeId` and a unique `Idempotency-Key`, or cancel through `POST /api/quotes/{quoteId}/cancel`.
3. `POST /api/atms/{id}/deposits` with recognized `notes` and `unrecognisedCount`. Move the ATM to `MAINTENANCE`, then `POST /api/atms/{id}/reconciliations` with all five cassette counts and `X-User`. If approval is required, a different `X-User` calls `POST /api/reconciliations/{reconciliationId}/approve`.
4. Read `GET /api/atms/{id}/report`, `GET /api/atms/{id}/consistency`, and `GET /api/exchange-rates`. The report includes per-cassette count, reserved and available notes, totals by currency, and an overall TRY balance. Business errors use ProblemDetails with a meaningful `code` and trace ID.

The following request bodies show the exact JSON shapes. Replace `{atmId}`, `{quoteId}`, and `{reconciliationId}` with IDs returned by earlier calls. Enum values are strings.

```http
POST /api/atms
Content-Type: application/json

{"name":"Branch ATM 01"}
```

```http
PUT /api/atms/{atmId}/configuration
Content-Type: application/json

{
  "cassettes": [
    {"number":1,"kind":"Recycling","currency":"TRY","denomination":50},
    {"number":2,"kind":"Recycling","currency":"TRY","denomination":20},
    {"number":3,"kind":"Recycling","currency":"USD","denomination":100},
    {"number":4,"kind":"Recycling","currency":"EUR","denomination":100},
    {"number":5,"kind":"DepositOnly","currency":null,"denomination":null}
  ]
}
```

```http
POST /api/atms/{atmId}/load
Content-Type: application/json
X-User: setup-officer

{
  "notes": [
    {"cassetteNumber":1,"currency":"TRY","denomination":50,"count":10},
    {"cassetteNumber":2,"currency":"TRY","denomination":20,"count":10},
    {"cassetteNumber":3,"currency":"USD","denomination":100,"count":10},
    {"cassetteNumber":4,"currency":"EUR","denomination":100,"count":10}
  ]
}
```

After `POST /api/atms/{atmId}/state` with `{"state":"IN_SERVICE"}`, request `{"cardNumber":"4111111111111111","currency":"TRY","amount":80}` at the quote endpoint. The response contains `id`, `expiresAt`, and `alternatives`; each alternative has an `id`, amount, kind, and cassette-level note allocation. To confirm, submit `{"alternativeId":"<selected alternative id>"}` with an `Idempotency-Key` header. The server returns the selected amount and dispensed notes. To deposit, submit `{"notes":[{"currency":"TRY","denomination":50,"count":2}],"unrecognisedCount":1}`. The response separates accepted allocations from recognized overflow and reports the count of unrecognized returned notes.

For reconciliation, switch to maintenance and submit all five cassette counts, including empty `notes` arrays for empty cassettes:

```http
POST /api/atms/{atmId}/reconciliations
Content-Type: application/json
X-User: count-officer

{
  "cassettes": [
    {"cassetteNumber":1,"notes":[{"currency":"TRY","denomination":50,"count":10}]},
    {"cassetteNumber":2,"notes":[{"currency":"TRY","denomination":20,"count":10}]},
    {"cassetteNumber":3,"notes":[{"currency":"USD","denomination":100,"count":11}]},
    {"cassetteNumber":4,"notes":[{"currency":"EUR","denomination":100,"count":10}]},
    {"cassetteNumber":5,"notes":[]}
  ]
}
```

This sample assumes no earlier withdrawal or deposit changed these particular counts. In an actual sequence, read the ATM report immediately before counting and use the physical values observed then. A one-note USD 100 difference is TRY 4,000 at the seeded rate, so the response should be `PendingApproval`; `X-User: review-officer` can approve it. The API records the physical adjustment when the count is submitted and blocks return to service until approval.

The lifecycle is `UNCONFIGURED -> CONFIGURED -> LOADED -> IN_SERVICE <-> MAINTENANCE`. Initial configuration and loading happen before service; later configuration, manual loading/unloading, and counting require maintenance. Withdrawals and customer deposits require `IN_SERVICE`. An invalid transition or operation in the wrong state returns a business error rather than silently changing stock. For example, an attempted quote in maintenance returns `409` with `code: INVALID_ATM_STATE`; self-approval of a pending reconciliation returns `409` with `code: SELF_APPROVAL_FORBIDDEN`. The ProblemDetails response also includes a human-readable detail and trace ID.

### Requirements and verification

| Assignment requirement | Implementation and evidence | Status |
| --- | --- | --- |
| A1-A7, B1-B2: currencies, denominations, five cassettes, capacities, lifecycle, multiple ATMs | Domain validation, setup and maintenance HTTP flows, duplicate-denomination cassette allocation tests | Implemented |
| A8, B9: count and reconcile every cassette, TRY conversion, four-eyes approval | Physical count and 999/1000/1001 TRY boundary integration tests; different `X-User` required above the threshold | Implemented |
| A9-A10, B3-B6: optimal withdrawal, quote/confirm/cancel, expiry, card limits, concurrency | Eight specified acceptance tests; quote and idempotency tests; parallel ATM and cross-ATM card tests | Implemented |
| A11, B7: deposit routing, overflow, returned/unrecognized notes | Deposit and boundary integration tests against PostgreSQL | Implemented |
| A12, B8: per-ATM balances, immutable movements, consistency endpoint | Reporting and ledger integration tests, SQL update/delete protection | Implemented |
| B10: network reporting and low-cash alert outbox | Per-ATM counts and totals exist; network aggregate and alert pipeline remain open | Partial |
| Technical: API, EF migrations, PostgreSQL, DI, Compose, OpenAPI, ProblemDetails, health | Application startup and infrastructure; clean final Compose verification remains open | Implemented, final smoke check pending |
| Delivery: ordered Postman run, final repository/ZIP | Local draft exists; repository handoff and clean end-to-end run remain open | Pending |

This table describes implemented code and existing automated evidence, not a claim that every delivery gate has been completed. The current detailed task ledger is `WORK_ITEMS.md`.

## Architecture and key design decisions

The solution separates `AtmCash.Domain` (rules and banknote selection), `AtmCash.Infrastructure` (EF Core, PostgreSQL migrations, and transactional operations), `AtmCash.Api` (HTTP, dependency injection, OpenAPI, ProblemDetails, and health checks), and `AtmCash.Tests` (unit and real-database integration tests). This keeps business rules independent of HTTP and persistence while retaining one deployable API.

**Notes and cassettes.** TRY, USD, and EUR support 20, 50, 100, and 500 banknotes. Every ATM has four single-denomination recycling cassettes and one mixed deposit-only cassette, each capped at 100 notes; one or two recycling cassettes must be foreign-currency cassettes. Deposit-only notes are never dispensed. The bounded dynamic-programming selector searches reachable sums using available stock and minimizes the note count. Since denominations are multiples of ten, amounts are scaled by ten. If an exact amount is impossible, it returns the nearest positive lower and nearest eligible upper alternatives. A greedy largest-note-first algorithm fails several required cases. When identical denominations occupy multiple recycling cassettes, the higher fill ratio is dispensed first.

**Concurrency and reservations.** A quote lasts 60 seconds. Physical notes offered by alternatives are reserved as a per-cassette maximum of each alternative's needs, because only one alternative can be confirmed. A hosted worker releases expired quotes; cancellation and state changes release reservations too. PostgreSQL transaction-scoped advisory locks serialize ATM and card operations across API instances, preventing double reservation at one ATM and cross-ATM daily-limit races. The lock order is consistent across operations. Cash changes and movement entries commit together.

**Limits and idempotency.** A card's UTC-day withdrawal limit is tracked separately for TRY 20,000, USD 1,000, and EUR 1,000 across the network. Active reservations count toward the limit. Only a keyed HMAC-SHA256 card fingerprint is stored, not the raw number. Confirmation requires `Idempotency-Key`; an identical replay returns the saved result without debiting again, while reuse with different data returns a conflict.

**Deposits and ledger.** Recognized notes first enter a matching recycling cassette; overflow enters the deposit-only cassette; notes that cannot fit are returned. Unrecognized notes are returned and excluded. Every stock or reservation change writes an immutable movement: `LOAD`, `UNLOAD`, `WITHDRAWAL`, `DEPOSIT`, `RESERVATION`, `RESERVATION_RELEASE`, or `RECONCILIATION_ADJUSTMENT`. Stored balances support efficient reports, while the consistency endpoint reconstructs counts from movements under a repeatable-read snapshot. A database trigger rejects movement updates and deletes.

**Reconciliation.** Maintenance requires an officer's physical count for every cassette. Signed count differences, stock adjustments, immutable movements, absolute TRY values, and exchange-rate snapshots are recorded in one transaction. Absolute values are summed, so gains cannot cancel shortages. Above TRY 1,000, reconciliation stays pending and blocks return to service until another `X-User` approves it. TRY=1, USD=40, and EUR=45 are seeded illustrative rates.

### Data and transaction boundaries

Each ATM owns five cassette configurations. Cassette stock rows keep physical and reserved note counts separately; availability is `count - reserved`. Deposit-only stock can contain multiple currency/denomination pairs. Quotes own alternative allocations and reservations, while card daily usage tracks confirmed and active reserved amounts for one fingerprint, currency, and UTC date. Reconciliations own count lines with expected and actual quantities plus the exchange rate used at count time. Movements carry cassette, currency, denomination, operation ID, physical-count delta, reservation delta, actor, and timestamp.

The request handler begins a database transaction, obtains its advisory locks, reads and validates current state, writes stock/usage/ledger changes, and commits. A rejected operation leaves both materialized counts and movements unchanged. The movement ledger is append-only; a correction requires a new movement rather than editing history. The separate consistency endpoint compares reconstructed physical and reserved counts with stored counts, making divergence observable. This is why both dimensions of stock must be represented in movement data.

The quote path acquires state, card, and ATM locks in a fixed sequence. The state lock coordinates quote creation with the transition into maintenance. The card lock protects a cross-ATM limit; the ATM lock protects physical notes. Confirmation first locks its idempotency key, checks for a previous result, then serializes card and ATM changes before releasing unused reservations and debiting the chosen notes. Replaying the same key is safe even after the original quote has changed state.

### Automated checks

The B3 cases specifically exercise stock-constrained exact payment, nearest lower and upper choices, absence of an upper choice, and minimization of the number of notes. The independent exhaustive-search checks reduce the risk that the dynamic program and its test expectations share the same mistake. PostgreSQL-backed tests then cover migration/seed behavior, the full ATM lifecycle, quote expiration, same-key confirmation replay, cancellation, deposits and capacity, ledger immutability, concurrent reservations, network-wide daily limits, and second-user reconciliation approval. The 999, 1000, and 1001 TRY tests verify the strict `> 1000` condition and both positive and negative stock differences. The suite passed 54/54 in Release at the most recent run.

## Assumptions

- A day is a UTC calendar day across all ATMs. Limits are per card and currency. For a quote with alternatives, its highest offered amount reserves the daily limit; unused allowance is released.
- If no lower payable amount exists, no zero-value alternative is offered. The upper alternative is omitted when it exceeds the remaining daily limit.
- An ATM becomes `LOADED` when at least one recycling cassette receives initial cash. Not every cassette must contain cash. Reconfiguration requires empty cassettes so ledger history stays meaningful.
- Entering maintenance cancels active quotes. Reconciliation adjustments take effect when physical counts are recorded; second-user approval only releases the service-return lock.
- `X-User` identifies an officer for audit and four-eyes comparison; it does not authenticate them. Seeded exchange rates are case-study data, not live market rates.
- When a supported note matches more than one recycling cassette on deposit, the less-filled cassette is filled first, with cassette number as a deterministic tie-breaker. On withdrawal, the higher fill ratio is used first as the assignment explicitly requires.
- A reconciliation above the threshold is based on the sum of the absolute TRY value of each note-type difference, using the rate captured when the count is recorded. Exactly TRY 1,000 completes without second approval; TRY 1,001 requires it.

## Unfinished items and known limitations

- B10's network-wide reporting and low-cash outbox/hosted alert service are pending. The implemented report covers one ATM and its cassettes.
- The planned low-cash threshold is an available recycling-cassette count below 10 notes, since cassette capacity is 100. Alert creation, transactional outbox persistence, delivery retries, and sent-state tracking are not implemented yet.
- A Postman collection and local environment have been prepared separately but have not yet been added to this Git repository or verified against a clean Compose run. The repository-delivered ordered end-to-end Postman scenario remains pending.
- Final clean Docker/Postman verification and a full API edge-case review remain pending. The 54-test PostgreSQL Release run verifies current code but is not the final clean-stack acceptance run.
- Local Compose credentials, development HMAC key, and seeded rates are demonstration settings. There is no production authentication, authorization, card-network integration, or secret management.

`WORK_ITEMS.md` tracks atomic delivery status. `ROADMAP.md` records the full assignment plan. Final submission requires the Git repository or ZIP and the Postman collection/environment.

## AI tools used

OpenAI Codex assisted with requirements analysis, architecture, code, migrations, test cases, and documentation. Its output was reviewed against the assignment PDF and corrected through the required eight B3 cases, independent exhaustive note-selection checks, real PostgreSQL tests, package/version alignment, and fixes to temporary test-database cleanup. Unfinished generated plans are reported as unfinished above.
