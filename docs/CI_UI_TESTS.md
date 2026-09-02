# CI — Normalized UI Test Runs

This document describes the canonical commands for running Harbor UI tests
locally and in CI pipelines.

## Test project layout

| Project | Scope |
|---|---|
| `tests/Harbor.App.Avalonia.Tests` | Headless Avalonia unit / inflation / parity tests |
| `tests/Harbor.E2E.App.Avalonia` | Full E2E UI tests (require real display / headless driver) |

## Categories

UI tests use TUnit `[Category]` attributes for filtering:

- `E2E` — end-to-end Avalonia UI scenarios.
- `Component` — granular component tests (toast, status-bar, chat, session list, onboarding, command palette, settings).
- `UI` — reserved for future headless-only UI unit tests.

## Local runs

```bash
# All Avalonia headless tests (fast, no display required)
dotnet test tests/Harbor.App.Avalonia.Tests -c Release --no-build

# View inflation only (headless)
dotnet test tests/Harbor.App.Avalonia.Tests --treenode-filter "/*/*/ViewInflationTests/*"

# E2E Avalonia tests (require Avalonia.Headless)
dotnet test tests/Harbor.E2E.App.Avalonia \
  --treenode-filter "/*/*/*/*[Category=E2E]"

# Component subset of E2E (AND of both categories)
dotnet test tests/Harbor.E2E.App.Avalonia \
  --treenode-filter "/*/*/*/*[Category=E2E][Category=Component]"
```

> **Note:** TUnit filtering is `--treenode-filter`; VSTest-style
> `--filter "Category=E2E"` is not supported under the MTP host. Category
> filters use the property syntax `[Category=X]` inside a treenode expression.

## CI pipeline (normalized)

A typical CI job-step block for Avalonia UI tests:

```yaml
- name: Build
  run: dotnet build Harbor.slnx -nologo -clp:NoSummary

- name: UI Headless Tests
  run: dotnet test tests/Harbor.App.Avalonia.Tests --nologo --no-build

- name: UI E2E Tests
  run: dotnet test tests/Harbor.E2E.App.Avalonia --treenode-filter "/*/*/*/*[Category=E2E]"
```

## JUnit XML output

To produce JUnit XML for CI upload:

```bash
dotnet test tests/Harbor.App.Avalonia.Tests \
  --logger "junit;LogFilePath=artifacts/avalonia-tests.xml"
```

## Pre-build hygiene gate

Run before building in CI to catch XAML style regressions:

```bash
bash ./tools/ui-hygiene.sh
```

## Post-change inspection

After any XAML / style / theme change, run:

```bash
bash ./tools/code-inspect.sh
```

This builds the solution, runs architecture tests, and runs the full
`Harbor.App.Avalonia.Tests` suite.
