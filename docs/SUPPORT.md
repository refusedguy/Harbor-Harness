# Getting help

This page tells you where to ask about Harbor and what to include so someone can
answer. Read the [troubleshooting index](./TROUBLESHOOTING.md) first for the
common build, provider, plugin, and renderer failures. Those symptoms already
have a check that resolves them.

## Where to ask

GitHub Issues is the support channel. Report a reproducible bug, request a
feature, or point out a documentation error in
[Harbor-Harness issues](https://github.com/refusedguy/Harbor-Harness/issues).
GitHub Discussions is not enabled for this repository.

Search existing issues before opening a new one, including closed ones. A
duplicate adds noise instead of getting a fix.

For behavior that may already be described, start with these pages:

- [Troubleshooting](./TROUBLESHOOTING.md) maps symptoms to checks.
- [FAQ](./FAQ.md) is keyed by the exact error text Harbor prints.
- [Environment variables](./ENV_VARS.md) lists every variable Harbor reads.
- [Configuration](./CONFIGURATION.md) explains config precedence.
- [Plugin development](./PLUGIN_DEVELOPMENT.md) covers loading and compile errors.

## Response expectations

Harbor is maintained on a best-effort basis. There is no guaranteed response
time and no support agreement. An issue with a short repro and the data listed
below gets answered sooner than a report that only says "it does not work". If
an answer or a fix does not arrive, add new evidence to the same issue instead
of opening another one.

## Collect the report data

Run these commands before you open an issue. Their output turns a description
into a reproducible report:

```bash
harbor --version
harbor logs --last
```

`harbor --version` prints the Harbor version and the .NET version in use.
`harbor logs --last` prints the most recent log file. Log files also live under
`~/.harbor/logs/`.

## What to include in a bug report

Copy this list into the issue body and fill each line:

- **Expected behavior.** What you expected Harbor to do.
- **Actual behavior.** What happened, including the exact error text.
- **Steps to reproduce.** The smallest sequence that triggers the problem.
- **Harbor version and install path.** The output of `harbor --version`, and
  whether you installed a release binary or built from source.
- **Environment.** Operating system, CPU architecture, .NET version, and the
  `HARBOR_TUI` value if you set one.
- **Provider and model.** The ids from `harbor providers` and
  `harbor models <provider>`. Do not paste API keys.
- **Logs.** The relevant lines from `harbor logs --last`. Remove tokens, keys,
  and file paths you do not want to share.

## Asking well

- State one problem per issue.
- Say which docs and issues you already read, and what you tried.
- Quote the exact error text. Searching by that text also finds existing
  answers.
- Do not tag maintainers in every comment. Answer follow-up questions instead.
- When someone resolves the question, confirm it and close the issue. If the
  answer does not work, say which part failed and add the new output.
