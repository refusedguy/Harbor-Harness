# Troubleshooting

This page maps a symptom you can observe to the check that resolves it. It is a
short index over the longer guides:
[getting started](./GETTING_STARTED.md#troubleshooting),
[build](./BUILD.md#troubleshooting-build), and
[development](./DEVELOPMENT.md#troubleshooting).

| Symptom | Check | Where to look |
|---|---|---|
| `dotnet build` reports "SDK not found" or a version below 10 | `dotnet --version` must resolve to 10.x. `global.json` pins the SDK band; install the .NET 10 SDK when the resolved version is older. | [Prerequisites](./DEVELOPMENT.md#prerequisites) |
| Restore fails with NU1603 or "package version not found" | Run `dotnet restore --force`, then check the pinned version in `Directory.Build.props` and the failing `.csproj`. | [Build troubleshooting](./BUILD.md#troubleshooting-build) |
| Clone is missing `contrib/external/ConsoleEx` | Clone with `--recurse-submodules`, or run `git submodule update --init --recursive`. The vendored tree is unmaintained and is not needed for the product build (ADR-019). | [Getting started](./DEVELOPMENT.md#getting-started) |
| "API key not found" for a provider | The env var must match `authEnvVar` in `providers/<name>.json`. Kilocode uses `KILO_API_KEY`, not `KILOCODE_API_KEY`. Check loaded providers with `harbor providers`. | [Provider configuration](./GETTING_STARTED.md#provider-configuration) |
| Provider request returns 404 | Check `baseUrl` and `modelsUrl` in `providers/<name>.json`, then confirm the id with `harbor models <provider>`. A 404 on the models endpoint means the gateway does not expose that path; the free-tier id may differ from the declared default. | [Add a new provider](./GETTING_STARTED.md#add-a-new-provider) |
| Session or plugin state is not written | Harbor writes under `~/.harbor/`: sessions in `sessions/` or `sessions.db`, plugins in `plugins/`, logs in `logs/`. The current user must be able to create and write that tree. | [Session management](./GETTING_STARTED.md#session-management) |
| Plugin fails to load with `CS0103`, `CS0246`, `CS0161`, or `CS1729` | Read the compiler line from the log: `grep -i plugin ~/.harbor/logs/harbor-cli-*.log`. `CS0103` is a missing `using`, `CS0246` is an assembly the runtime did not load, `CS0161` is a missing return, `CS1729` is an old API signature. | [Reading compilation errors](./PLUGIN_DEVELOPMENT.md#debugging-reading-compilation-errors) |
| TUI falls back to plain output or paints nothing | `HARBOR_TUI` must name a registered backend. `cellforge` is the fullscreen renderer and `plain` is the pipes/CI renderer; an unknown value falls back with a warning in the log. Unset the variable to use the default. | [Alternative UIs](./ALTERNATIVE_UIS.md) |

For failures that are not in this table, read the most recent log with
`harbor logs --last` or the files under `~/.harbor/logs/`. If the checks here
do not resolve the failure, [ask for help](./SUPPORT.md) with the version,
environment, and log output listed there.
