# Contributing to AegisOps

Thanks for your interest! AegisOps is a .NET 10 + React control plane; the design lives in [`docs/`](docs/README.md).

## Getting started

```bash
scripts/gen-secrets.sh            # writes a local .env (never commit it)
docker compose up --build -d      # API :8080, web :3000 (both on 127.0.0.1)
docker compose logs api | grep 'Development user'   # first-run demo passwords; you must change them at first sign-in
```

For local development without containers, `scripts/dev-up.sh` starts only PostgreSQL and Redis.

## Before you open a pull request

- `dotnet build AegisOps.slnx` (warnings are errors) and `dotnet test AegisOps.slnx`
- In `src/AegisOps.Web`: `npm run lint`, `npm run format:check`, `npm run build`, `npm run test:e2e`
- New API routes are added to `AuthorizationMatrixTests` with their docs/09 permission, and every state change
  writes an audit event (`AuditCoverageTests`).
- Update the relevant `docs/*.md` and add a line to `CHANGELOG.md` for user-visible changes.

Use [Conventional Commits](https://www.conventionalcommits.org/) (`feat(policies): …`, `fix(api): …`) and keep
pull requests focused. Security issues go through [SECURITY.md](SECURITY.md), not public issues.

## Code of conduct

Be respectful and constructive. Harassment or discrimination of any kind is not tolerated; maintainers may remove
comments or contributors that break this rule.
