# Security policy

API Arena is a learning/portfolio project. It is **not** guaranteed to be free of vulnerabilities.

## Reporting a vulnerability

Please report suspected vulnerabilities privately — do not open a public issue.

- Chosen channel: this repository's **Security → Advisories → Report a vulnerability** (GitHub private vulnerability reporting).
- **Release blocker:** no hosted repository or enabled reporting channel has been verified yet. Before publication, enable private vulnerability reporting in repository settings and verify the private form and maintainer notifications. [GitHub setup instructions](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/configure-vulnerability-reporting/configure-for-a-repository).
- Include: affected component (control API, simulator, worker, frontend), reproduction steps, impact. Never include real credentials or personal data.
- Please allow reasonable time for a fix before disclosure.

## Scope notes
- The simulator intentionally returns errors (503, 429, slow responses). Those are scenario behaviour, not vulnerabilities. Platform responses carry an `X-Arena-Platform` header.
- Outbound webhooks are disabled by default and must stay disabled in production until network egress restrictions are deployed and verified (docs/runbooks/EGRESS.md).
- Do not run load or attack tests against third-party systems through API Arena.

## Security documentation
- Threat model: [docs/THREAT_MODEL.md](docs/THREAT_MODEL.md)
- Latest review and residual risks: [docs/SECURITY_REVIEW.md](docs/SECURITY_REVIEW.md)
- Password/token policy: minimum 10 characters (≥4 distinct), lockout 5 failures / 15 min, reset token 30 min single-use, e-mail verification token 24 h, sessions server-side (logout revokes immediately), idle timeout 8 h.
