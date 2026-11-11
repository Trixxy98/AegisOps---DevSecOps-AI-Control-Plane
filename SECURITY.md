# Security policy

## Supported versions

| Version | Supported |
| --- | --- |
| 1.0.x | Yes |
| < 1.0 | No |

AegisOps v1 is intended for **evaluation and demos on a single machine** (the Compose stack binds to
`127.0.0.1`). It is not yet hardened for production or multi-tenant use; see
[docs/security-review-v1.md](docs/security-review-v1.md) and the known gaps in
[docs/17-security-hardening.md §8](docs/17-security-hardening.md).

## Reporting a vulnerability

Please **do not open a public issue** for security problems. Use GitHub's private vulnerability reporting
instead: open the repository's **Security** tab and choose **Report a vulnerability**.

Include the affected version or commit, the steps to reproduce, and the impact you observed. You can expect an
acknowledgement within a few days. Fixes are released as patch versions and credited in the changelog unless you
ask otherwise.

## Handling secrets

Never include real credentials, tokens or customer data in issues, pull requests or test fixtures. The project
uses only synthetic data (for example `AKIA` followed by placeholder characters).
