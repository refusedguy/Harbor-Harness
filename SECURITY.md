# Security Policy

## Supported versions

| Version | Supported |
| ------- | --------- |
| `dev` (pre-1.0 development) | Yes |
| `master` (latest release) | Yes |
| Older tags / feature branches | No — upgrade to the latest `master` first |

Security fixes land on `dev` and ride the normal merge to `master`.
There is no separate LTS line before v1.0.

## Reporting a vulnerability

Do not open a public issue for a suspected vulnerability.
Use **GitHub Security Advisories** (private) for this repository:
`Security` tab → `Advisories` → `Report a vulnerability`.

If advisories are unavailable to you, contact the maintainers through the
channels listed in [AGENTS.md](./AGENTS.md) and ask for a private channel —
do not paste secrets, tokens, or exploit details into a public issue.

Please include:

- Affected component and version or commit (`Harbor.Plugins.*`, `Harbor.Ipc.*`, provider client, TUI renderer, …)
- Steps to reproduce (commands, config, minimal plugin or MCP server fixture)
- Impact assessment (what an attacker gains: code execution, secret exfiltration, persistence, …)
- Any logs or redacted output (`harbor logs --last` — redact API keys first)

## Response expectations

- Acknowledgement within **3 business days**.
- Triage with a severity verdict within **7 business days**.
- Fixes for high severity are prioritised ahead of feature work; lower-severity
  findings are tracked as issues with an explicit rationale.

You will be credited in the advisory unless you ask not to be.

## Scope notes

The largest trust surfaces are documented, not assumed safe:

- **CS-source plugins** compile via Roslyn and run in-process with full trust.
  Only install plugin source you have reviewed. See
  [Plugin Development](./docs/PLUGIN_DEVELOPMENT.md).
- **MCP servers** are untrusted child processes over stdio. See
  [Tools Catalog](./docs/TOOLS_CATALOG.md).
- **Credentials** live in `~/.harbor/config.json` and environment variables
  (for example `ANTHROPIC_API_KEY`, `KILO_API_KEY`). Never paste them into
  issues, logs, or shared sessions.
- Background analysis: [Security and Sandboxing Analysis](./docs/security-sandboxing-analysis.md).
