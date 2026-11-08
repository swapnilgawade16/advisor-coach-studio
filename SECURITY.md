# Security policy

## Reporting a vulnerability

Please **do not** open a public issue for security problems. Use GitHub's
[private vulnerability reporting](https://docs.github.com/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability)
("Report a vulnerability" on the repository's **Security** tab). Include steps to reproduce, affected versions, and
impact. We aim to acknowledge reports within five working days.

## Scope highlights

Reports are especially welcome for:

- Azure credentials, tokens, endpoints, or prompts reaching the browser.
- Cross-session data access (one anonymous browser reading another's session).
- WebSocket handshake bypasses (Origin, cookie, subprotocol, ownership checks).
- CSRF on state-changing endpoints.
- Transcript or audio content leaking into logs or telemetry.

## Deployment guidance

- Store `AccessGate__SharedCode` and API keys in Key Vault or protected app settings; prefer Entra ID (managed identity).
- Serve only over HTTPS; keep `Security__UseForwardedHeaders` enabled only behind a trusted proxy.
- Rotate the access code by also incrementing `AccessGate__CodeVersion`.
- Run a single instance (in-memory state).
