# FAQ

This page is indexed by the text Harbor prints. Copy the message you see into
this page's search. A variable part (a tool name, provider id, model id, or
path) is written as `<name>` in a heading. If the failure is a symptom rather
than a quoted message, use the [troubleshooting index](./TROUBLESHOOTING.md).
For support channels and the data to attach to a report, use
[getting help](./SUPPORT.md).

## `API key not found. Set $NAME or pass --provider-api-key.`

The provider has no credential in the environment at request time. Set the
variable named in the message, or store the key with
`harbor auth set <provider> <key>`. The variable comes from `authEnvVar` in
`providers/<provider>.json`. Kilocode uses `KILO_API_KEY`, not
`KILOCODE_API_KEY`; the wrong spelling is the most common cause of this one.
See [provider credentials](./ENV_VARS.md#provider-credentials) and
[provider configuration](./GETTING_STARTED.md#provider-configuration).

## `No API key for 'provider'.`

The auth store has no key for that provider id in the config file or the
environment. Run `harbor auth set <provider> <key>`, or set one of the env
vars the message lists. Environment overrides are described in
[configuration](./CONFIGURATION.md#6-environment-variable-overrides). Confirm
the id with `harbor providers` before changing config, since a typo looks the
same as a missing key.

## `Unknown tool: 'name'.`

The model called a tool name that is not registered. Check the spelling first.
If the tool comes from an MCP server, confirm the server started and its
`tools/list` succeeded; if it comes from a plugin, confirm the plugin compiled.
The structured code on this failure is `TOOL_NOT_FOUND`, so policies can branch
on the code instead of the message. See
[tool error codes](./TOOL_ERROR_CODES.md#the-vocabulary) and the compiler
entries below.

## `Model 'model' not found in provider 'provider'.`

The configured model id is not in the provider's catalog. Run
`harbor models <provider>` to list the ids the gateway actually returns. A 404
from the models endpoint means `baseUrl` or `modelsUrl` in the provider config
is wrong; the free-tier id can also differ from the declared default. See the
404 row in [troubleshooting](./TROUBLESHOOTING.md).

## `Unknown HARBOR_TUI 'value'; falling back to 'backend' (the single fallback rule).`

The value is not a registered renderer id. Use `cellforge` for the fullscreen
renderer or `plain` for pipes and CI. `consoleex` is a legacy alias for
`cellforge`. An unknown value does not stop Harbor; it falls back with a
warning in the log. See [environment variables](./ENV_VARS.md#desktop-and-tui)
and [alternative UIs](./ALTERNATIVE_UIS.md).

## `Unknown renderer backend 'id'.`

A `/renderer` swap asked the pipeline for an id it does not know. The swap is
not applied. Use one of the ids from the renderer registry and retry, or unset
the override to return to the default backend. See
[alternative UIs](./ALTERNATIVE_UIS.md).

## `TOOL_PERMISSION_DENIED: Permission denied`

A permission rule refused the call. This is a decision, not a malfunction, so
the tool never ran and no state changed. The agent's ruleset carries the rule;
read the [permissions recipes](./EXAMPLES.md#permissions) for the config
shape. The other coded outcomes, including `TOOL_NOT_STARTED` and
`TOOL_OUTCOME_UNKNOWN`, are listed in
[tool error codes](./TOOL_ERROR_CODES.md#the-vocabulary).

## `CS0103: The name 'X' does not exist in the current context`

A CS-source plugin is missing a `using`. Add the namespace of the type in the
error: `System.Net.Http` for `HttpClient`, `System.Text.Json` for
`JsonDocument`, or `Microsoft.Extensions.Logging` for log extensions. The full
compile log is at `~/.harbor/logs/harbor-cli-*.log`. See
[reading compilation errors](./PLUGIN_DEVELOPMENT.md#debugging-reading-compilation-errors).

## `CS0246: The type or namespace name 'X' could not be found`

The type lives in an assembly the CS plugin loader does not reference. A
CS-source plugin can only use assemblies already loaded by Harbor, such as
`Harbor.Abstractions`, `Harbor.Terminal.Abstractions`, `System.Text.Json`, and
`CSharpFunctionalExtensions`. For anything else, ship a DLL plugin. See
[reading compilation errors](./PLUGIN_DEVELOPMENT.md#debugging-reading-compilation-errors).

## `CS0161: 'X': not all code paths return a value`

A plugin method returns on one branch and falls off the end on another. Add a
final `return` that covers the remaining paths, rather than returning `null`
or throwing for an expected input. See
[reading compilation errors](./PLUGIN_DEVELOPMENT.md#debugging-reading-compilation-errors).

## `CS1729: 'X' does not contain a constructor that takes N arguments`

The plugin calls an older constructor signature. Check the current `IPlugin`
and tool contracts in `src/Harbor.Abstractions/Plugins/` before changing the
call. See
[reading compilation errors](./PLUGIN_DEVELOPMENT.md#debugging-reading-compilation-errors).

## `SDK not found`

The exact .NET message names the version and the `global.json` path. Harbor
pins the .NET 10 SDK band, so a resolved 9.x SDK produces this class of error.
Run `dotnet --version` and install the 10 SDK when it reports an older version.
See [prerequisites](./DEVELOPMENT.md#prerequisites) and the SDK row in
[troubleshooting](./TROUBLESHOOTING.md).

## `NU1603`

NuGet resolved a different version than the one a project asked for. Run
`dotnet restore --force`, then compare the pinned version in
`Directory.Build.props` and the failing `.csproj`. See
[build troubleshooting](./BUILD.md#troubleshooting-build).
