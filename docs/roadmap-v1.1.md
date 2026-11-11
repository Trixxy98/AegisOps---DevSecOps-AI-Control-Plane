# Roadmap — v1.1: production readiness

v1.0 is for evaluation and demos on one machine (see README, "Intended use and limits"). v1.1 makes AegisOps
something another team can run for real. Estimates are working days for one developer.

| # | Item | Why | Estimate |
| --- | --- | --- | --- |
| 1 | **Production profile.** `docker-compose.prod.yml` with `ASPNETCORE_ENVIRONMENT=Production`, a one-shot `migrate` command or init container (migrations are refused outside Development today), no demo seed | AegisOps cannot start outside Development mode | 1 |
| 2 | **First-admin bootstrap.** `aegisops admin create --email …` (or env-driven, single use) that creates the first Admin with a temporary password; audited | Without the Development seed there is no way to create the first user | 0.5 |
| 3 | **TLS and reverse proxy guide.** Caddy/nginx example with HTTPS and HSTS; `UseForwardedHeaders` with a known-proxy list so client IPs (rate limits, audit) are correct | Today all browser users share nginx's IP and traffic is plain HTTP on loopback | 1 |
| 4 | **Rate limits per principal.** Global limiter partitioned by user or API key, an `uploads` policy for scans, `Retry-After` headers | Only login/refresh and AI are limited (docs/17) | 0.5 |
| 5 | **Errors as Problem Details.** `AddProblemDetails` + `UseExceptionHandler` with `traceId`; no developer exception page in the production profile | Unhandled errors return empty 500s or stack traces | 0.5 |
| 6 | **Audit tamper protection.** Separate database roles: the app may only `INSERT`/`SELECT` on `audit.*` | docs/17 §2 promise not yet implemented | 0.5 |
| 7 | **Regex hardening and stored-AI-content encryption.** `NonBacktracking` + timeouts for policy globs and AI blocked patterns; Data Protection column encryption for `prompt_content`/`response_content` | docs/17 §3, §5 | 1 |
| 8 | **Bring your own repositories.** Target-repo workflow and `aegisops-report` action documented for any GitHub owner (no `Trixxy98` references), versioned action tag (`@v1`) instead of `@dev` | Others cannot reuse the CI integration as-is | 1 |
| 9 | **Operations guide.** Backup/restore (`pg_dump`), upgrades and migrations policy, secret rotation (JWT key ring, webhook keys), sizing | Running it for months needs this | 1 |
| 10 | **CI scope.** Semgrep `p/owasp-top-ten` with `--error`, `npm audit --audit-level=high`, `dotnet list package --vulnerable`; multi-arch images (amd64 + arm64) | docs/17 §7 | 0.5 |

**Total: about 7–8 days.** Items 1–3 are the minimum for a team to self-host behind HTTPS; 4–7 close the
remaining v1 security-review items; 8–10 make it maintainable and reusable.
