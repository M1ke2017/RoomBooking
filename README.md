# Daylo

## What is Daylo?

Daylo is a lightweight field-team coordination app that helps managers plan daily work, react to urgent incidents,
reschedule tasks and stay in touch with workers from one web interface.

The manager opens one page, **/today**, and sees:

- **Pilne**: urgent incidents waiting for a decision;
- **Plan dnia**: the plan of the day;
- **Zespół**: who is working and where, with a call button;
- **Nieprzypisane**: tasks that still need a worker.

It is built for a phone first and works just as well on a desktop.

## The problem

A field team's day never goes to plan. A customer reports a failure, the only people with the right skills are busy,
and the manager has a few minutes to decide who goes and what moves. Today that means Excel, several calendars,
phone calls and WhatsApp. Daylo keeps the plan, the people and the decision in one place.

## The main flow (what Daylo is growing towards)

1. A customer reports an urgent incident.
2. Daylo finds the workers with the right skills and checks their plan and the resources needed.
3. It detects the conflict and proposes the best person.
4. It names a less urgent task to move and a new time for it, and shows the impact.
5. The manager approves. The plan is updated in one transaction, the screen changes live, and the decision is kept in
   a simple history.

At most one task is moved per decision. Daylo never moves A, then B, then C.

## Status: v0.1 foundation

| Done | Next |
|---|---|
| The data model, PostgreSQL, the first migration | Analyze an incident: skills, conflicts, the best worker |
| An idempotent demo day, with one conflict built in | The reschedule proposal and Accept (transactional, 409 when stale) |
| `/today`: urgent incidents, the plan, the team, unassigned tasks | The decision history on screen, and a simple report |
| `/health` | Live refresh, once `/today` needs it |

## Run it locally

Requirements:

- the [.NET 10 SDK](https://dotnet.microsoft.com/download);
- [Docker Desktop](https://www.docker.com/products/docker-desktop/), running.

```bash
dotnet run --project src/Daylo.AppHost
```

Aspire starts PostgreSQL in Docker and the Daylo app connected to it. The console prints the dashboard link. In the
dashboard, the **daylo-web** resource turns *Running* and healthy; click its link to open `/today`.

On startup in Development, Daylo applies its migrations and creates the demo day:

- 3 workers with skills and phone numbers;
- 2 customers and 3 sites;
- 4 resources, one of them out of service;
- 5 tasks: one completed, one in progress, two planned, one without a worker;
- 1 urgent incident: a hospital's power failure that needs an electrician while both electricians are busy.

The demo day is created once per day. Restarting changes nothing.

Without Aspire, point Daylo at any PostgreSQL:

```bash
ConnectionStrings__daylo="Host=localhost;Database=daylo;Username=…;Password=…" dotnet run --project src/Daylo.Web
```

Tests (Docker must be running):

```bash
dotnet test --solution Daylo.sln
```

## How it is built

Two projects carry the product; the third only starts it locally.

```
src/
  Daylo.Core      the business model: Worker, Skill, Customer, Site, WorkTask, Incident, Assignment, Resource,
                  ActivityLog, and the team's working day (no packages)
  Daylo.Web       ASP.NET Core + Blazor (server-rendered pages), EF Core + PostgreSQL, the demo data
  Daylo.AppHost   .NET Aspire: PostgreSQL in Docker + Daylo, one command
tests/
  Daylo.Tests     the app in-process against a real PostgreSQL (Testcontainers)
```

The rules it follows:

- **One app, one database.** No microservices, no message broker, no separate reporting service.
- **Entities go straight to the screen.** No DTO or mapper layers.
- **The database guards decisions.** Tasks and incidents carry a row version (PostgreSQL `xmin`), so two managers
  cannot both win the same decision.
- **Business names.** A task is `WorkTask` in code only so it never collides with .NET's `Task`.

## Technology

.NET 10, ASP.NET Core, Blazor (static server rendering), EF Core 10 with Npgsql, PostgreSQL, .NET Aspire for the local
run, xUnit v3 and Testcontainers.

## History

Daylo replaces CrewCall, an earlier version of this project that grew too much architecture for its product. CrewCall
is kept in git history only, on the branch `crewcall-final-archive`.
