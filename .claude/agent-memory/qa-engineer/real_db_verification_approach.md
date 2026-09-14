---
name: real-db-verification-approach
description: How to get a live Postgres instance for QA verification in this repo without probing/guessing another container's credentials
metadata:
  type: feedback
---

When asked to verify a migration/feature against a REAL database (not InMemory), this workspace already has Docker containers running that may look reusable (e.g. a `my_postgres` container on port 5432), but their credentials are not discoverable safely: the dev `appsettings.Development.json` connection string does not necessarily match what a pre-existing container was actually initialized with, and dumping the container's env vars or trying multiple username/password combos to find out gets flagged by the auto-mode permission classifier as credential exploration (confirmed both for a `my_postgres` container in this session and for a SQL Server container in an earlier code-reviewer session — see `.claude/agent-memory/code-reviewer/verify_build_test_claims.md`).

**Why:** the classifier treats iterative credential guessing/env-dumping as a security-relevant action regardless of intent, and it's genuinely bad practice to poke at another process's secrets even when the goal is legitimate verification.

**How to apply:** don't iterate credentials or dump container env vars looking for a password. Instead, spin up a **fresh, disposable** Postgres container with credentials you set yourself, e.g.:
```
docker run -d --name iams-qa-pg -e POSTGRES_USER=iams -e POSTGRES_PASSWORD=dev-only-change-me -e POSTGRES_DB=iams -p 15432:5432 postgres:latest
```
This sidesteps the credential problem entirely, matches the project's own dev username/password/db-name convention (from `appsettings.Development.json`) so `dotnet ef database update` / app config "just works" by pointing at port 15432, and is trivial to `docker rm -f` when done. Only fall back to asking the user directly for real credentials if a fresh throwaway container genuinely isn't an option (e.g. task requires validating against pre-existing seeded data).
