# ATM Cash Management Delivery Roadmap

This roadmap tracks the implementation of Parts A and B of `Project_Assignment_Software_Developer.pdf`. The assignment is the source of requirements; choices about ambiguous behavior are documented in `README.md`. All seven phases below are complete. `WORK_ITEMS.md` records the 32 smaller steps and `VERIFICATION.md` records the final clean-stack results.

## Phase 0: foundation (G01-G05)

Set up the .NET 10 solution, separate domain/infrastructure/API/test projects, pin dependencies, and provide PostgreSQL 17, EF Core migrations, Docker Compose, OpenAPI, ProblemDetails, and health checks. Validate a fresh migration and seeded exchange rates on a real database.

## Phase 1: ATM lifecycle and cassettes (G06-G09)

Implement multiple ATMs, five-cassette configuration, supported currencies and notes, manual cash loading/unloading, state transitions, immutable movement records, and a ledger consistency endpoint. Test setup, capacity, invalid states, maintenance, and rollback through HTTP against PostgreSQL.

## Phase 2: optimal note selection (G03, G10-G13)

Use bounded dynamic programming over available stock to minimize the number of dispensed notes. Return an exact result or nearest lower and upper alternatives, subject to the card's remaining daily limit. Verify all eight required B3 examples and compare small stock configurations with an independent exhaustive search. When two recycling cassettes hold the same note, dispense from the fuller cassette first.

## Phase 3: two-step withdrawal (G14-G20)

Persist 60-second quotes and reserve the union of offered cassette allocations. Confirm one alternative with a required idempotency key or cancel the quote. Release unused, cancelled, expired, or maintenance-cancelled reservations. Track per-card UTC-day limits across the network without storing raw card numbers. Use PostgreSQL transaction-scoped advisory locks and parallel integration tests to prevent double reservation and cross-ATM limit races.

## Phase 4: deposits and reconciliation (G21-G25)

Route recognized deposits to matching recycling cassettes, then the mixed deposit-only cassette, returning overflow and unrecognized notes. Record movements only for accepted notes. In maintenance, record physical counts for all five cassettes, adjustment movements, and exchange-rate snapshots. Sum absolute TRY differences; amounts above TRY 1,000 require a different `X-User` before returning to service. Verify the 999/1,000/1,001 TRY boundaries.

## Phase 5: reporting and alerts (G26-G28)

Report physical, reserved, and available note counts per cassette and ATM, currency totals, and the network TRY total. Create a transactional low-cash outbox event when a recycling cassette drops below 10 available notes. A hosted worker logs pending events, marks them sent, and retries failures. The alert delivery semantics and demonstration-only settings are disclosed in `README.md`.

## Phase 6: acceptance and handoff (G29-G32)

Deliver an ordered Postman collection and environment in `docs/postman`, verify invalid-request ProblemDetails, and run the full Release suite and Postman flow against a fresh Compose project and PostgreSQL volume. The final run passed 56 tests and 22 Postman requests. Keep the README's run instructions, architecture rationale, assumptions, limitations, AI-use note, and requirement traceability current. The repository owner supplies the Git repository link; a ZIP was declined.

## API map

| Operation | Endpoint |
| --- | --- |
| ATM create, list, and details | `POST /api/atms`, `GET /api/atms`, `GET /api/atms/{id}` |
| Cassette configuration | `PUT /api/atms/{id}/configuration` |
| State transition | `POST /api/atms/{id}/state` |
| Manual load and unload | `POST /api/atms/{id}/load`, `POST /api/atms/{id}/unload` |
| Quote and confirmation/cancellation | `POST /api/atms/{id}/quotes`, `POST /api/quotes/{id}/confirm`, `POST /api/quotes/{id}/cancel` |
| Deposit | `POST /api/atms/{id}/deposits` |
| Physical count and second-user approval | `POST /api/atms/{id}/reconciliations`, `POST /api/reconciliations/{id}/approve` |
| ATM and network reports | `GET /api/atms/{id}/report`, `GET /api/report` |
| Ledger consistency | `GET /api/atms/{id}/consistency` |
| Exchange rates and health | `GET /api/exchange-rates`, `GET /health` |
