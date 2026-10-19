# CrewCall — portfolio release checklist

Checked for the Sprint 17 release. Each item says how it was checked, so it can be repeated.

- [x] **Clean build.** `dotnet clean`, `dotnet restore`, `dotnet build CrewCall.sln`: 0 errors, 0 warnings. Warnings are
  errors (`Directory.Build.props`).
- [x] **All tests green.** `dotnet test --solution CrewCall.sln` (Docker running), and the CI workflow on the pull
  request.
- [x] **Local Aspire run.** `dotnet run --project src/CrewCall.AppHost` from a fresh copy of the repository with a new
  data volume:
  - all six resources start;
  - `/health` and `/alive` return 200 on the API, Integrations, Reporting and Web;
  - the migrations are applied.
- [x] **Demo seed.** The demo day is created on startup. A second seed (restart, or `POST /api/demo/seed`) creates
  nothing.
- [x] **Primary scenario.** In Chromium, on `/operations`: incident → Analizuj → Pokaż propozycję → Zatwierdź zmianę.
  Then check that:
  - the moved visit, the alerts and the report changed;
  - a second window refreshed live.
- [x] **Responsive check.** 1366 px, 768 px and 390 px: no horizontal scroll, and every action is reachable.
- [x] **README.** The product, the scenario, the 12-step demo flow, the architecture, how to run, screenshots,
  decisions, limitations and troubleshooting.
- [x] **Screenshots.** `docs/screenshots`: the dashboard, the proposal, the plan after approval, the report, the phone
  view and the Aspire runtime.
- [x] **No secrets.**
  - No passwords, keys or connection strings in the repository (`appsettings*.json` hold logging and tuning only).
  - Aspire generates the PostgreSQL and RabbitMQ credentials and keeps them in the AppHost user secrets.
- [x] **Package vulnerabilities checked.** `dotnet list package --vulnerable --include-transitive`: no vulnerable
  packages in any of the 22 projects.
- [x] **Deployment config reviewed.** See below.

## Deployment readiness review

The portfolio release runs locally through Aspire. Before any shared environment:

| Area | Today | Needed for a shared environment |
|---|---|---|
| Configuration | Aspire injects the connection strings and service addresses. `appsettings.json` holds logging and tuning only. | Provide `ConnectionStrings__crewcall`, `ConnectionStrings__crewcall-reporting` and `ConnectionStrings__crewcall-rabbitmq`, and the service addresses, from the platform. |
| Secrets | Generated Aspire parameters in the AppHost user secrets. Nothing is in the repository. | A secret store (for example Key Vault), with credentials per environment. |
| Migrations | The API and Reporting apply their EF Core migrations at startup, **only in Development**. | A migration step in the pipeline (`dotnet ef migrations bundle`, or an idempotent script) before the new version starts. |
| Demo data | `Demo:Seed` is set only by the AppHost. Without it, `/api/demo/seed` does not exist. | Leave it unset. |
| HTTPS | The web app uses `UseHttpsRedirection`, plus `UseHsts` outside Development. The AppHost's default profile is https. | TLS at the ingress, and HSTS kept on. |
| Developer endpoints | OpenAPI is mapped only in Development. `/live` and `/status` are developer pages. | Restrict or remove `/live` and `/status`. |
| Health | `/health` (readiness, including the database and broker) and `/alive` (liveness) on every service. | Wire them to the platform's probes. |
| Authentication | None (out of scope). | Sign-in and roles before anyone outside the team can reach it. |
