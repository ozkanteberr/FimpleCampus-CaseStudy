# Verification record

Date: 12 October 2026 (Europe/Istanbul).

## Release and database checks

- A fresh Docker Compose project (`atm-cash-g31`) built the current API image and started PostgreSQL 17 with a new, empty named volume. The database health check passed, EF Core migrations ran on API startup, and `GET /health` returned HTTP 200.
- The isolated project published the API on `127.0.0.1:5082` and PostgreSQL on `127.0.0.1:55433` to keep the existing local Compose project untouched. The repository's standard `compose.yaml` continues to use ports 5080 and 55432.
- `dotnet test AtmCashManagement.sln -c Release --no-restore` ran against this fresh PostgreSQL instance through `ATM_TEST_PG`. Result: 56 passed, 0 failed, 0 skipped. This includes the eight B3 acceptance cases, real-database flows, concurrency, reporting, reconciliation, and API error-contract checks.
- The repository Postman collection ran through Newman against the newly built Compose API with `baseUrl=http://127.0.0.1:5082`. Result: 22 requests passed, 0 failed. The ordered run covered ATM setup, cash load, service, quote/confirm/cancel, deposit, maintenance, physical count, second-user reconciliation approval, return to service, and reports.

## Delivery status

The verified collection and environment are in `docs/postman`. The README contains the final run instructions, architecture rationale, assumptions, limitations, AI-use note, and A1-A12/B1-B10 traceability. The repository owner performs the final commit and push; no ZIP is required. Local Compose credentials, seeded exchange rates, and the development card HMAC key are demonstration settings.
