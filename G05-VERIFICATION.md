# G05 — Initial Migration Verification

Verified on PostgreSQL 17 through Docker Compose.

- `docker compose up -d db` started a healthy PostgreSQL container.
- The integration test created an isolated database, applied the initial EF Core migration, verified the seeded TRY/USD/EUR rates (1/40/45), and confirmed that PostgreSQL rejected an update to a movement record. The test removed its temporary database afterward.
- The Release solution build completed with zero errors and zero warnings.
- `dotnet test AtmCashManagement.sln -c Release --no-build` passed all 27 tests, including the PostgreSQL integration test.
- `docker compose up -d --build api` built and started the API. On startup it applied `20261012092747_InitialCashManagement` to the Compose database.
- `GET /health` returned `Healthy`; `GET /openapi/v1.json` returned HTTP 200.

This is the historical G05 checkpoint. The final clean-stack results, including all ATM flows, are recorded in `VERIFICATION.md`.
