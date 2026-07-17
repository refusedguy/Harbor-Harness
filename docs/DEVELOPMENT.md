# Development Guide

## Prerequisites

- .NET 10 SDK (https://dot.net)
- Git
- Any modern IDE (VS Code, Rider, Visual Studio)

## Getting started

```bash
git clone https://github.com/harbor-sh/harbor
cd harbor
dotnet build
dotnet test
```

## Project structure

See [docs/ARCHITECTURE.md](./ARCHITECTURE.md) for the full layout.

## Build commands

```bash
# Build all projects
dotnet build

# Build a specific project
dotnet build src/Harbor.Core

# Build Release configuration
dotnet build -c Release

# Clean build artifacts
dotnet clean
```

## Test commands

```bash
# Run all tests
dotnet test

# Run a specific test project
dotnet test tests/Harbor.Core.Tests

# Run tests with detailed output
dotnet test --logger "console;verbosity=detailed"

# Run a specific test
dotnet test tests/Harbor.Abstractions.Tests --filter "FullyQualifiedName~IdentifiersTests"
```

Tests use [TUnit](https://github.com/thomhurst/TUnit). Test files are in `tests/<Project>.Tests/`.

## Running the CLI

```bash
# Set API key
export ANTHROPIC_API_KEY=sk-ant-...

# Run interactive REPL
dotnet run --project src/Harbor.Cli

# One-shot prompt
dotnet run --project src/Harbor.Cli -- ask "What is 2+2?"

# List providers
dotnet run --project src/Harbor.Cli -- providers

# List models
dotnet run --project src/Harbor.Cli -- models
dotnet run --project src/Harbor.Cli -- models anthropic

# Show help
dotnet run --project src/Harbor.Cli -- help
```

## Configuration

### Provider configs

Located in `providers/` (project-local) or `~/.harbor/providers/` (user-global):

```jsonc
{
  "id": "openrouter",
  "displayName": "OpenRouter",
  "baseUrl": "https://openrouter.ai/api/v1",
  "apiType": "openai-compatible",
  "authType": "bearer",
  "authEnvVar": "OPENROUTER_API_KEY",
  "modelsUrl": "https://openrouter.ai/api/v1/models"
}
```

### API keys

Set via env vars (conventional names):

```bash
export ANTHROPIC_API_KEY=sk-ant-...
export OPENAI_API_KEY=sk-...
export OPENROUTER_API_KEY=sk-or-...
export DEEPSEEK_API_KEY=...
export GROQ_API_KEY=gsk_...
# etc.
```

### Default model

```bash
export HARBOR_MODEL=anthropic/claude-sonnet-4-20250514
# or
export HARBOR_MODEL=openai/gpt-4o
# or
export HARBOR_MODEL=openrouter/anthropic/claude-3.5-sonnet
```

## Common development tasks

### Add a new builtin tool

1. Create `src/Harbor.Tools.Builtin/<Name>/<Name>Tool.cs`.
2. Implement `ITool` interface.
3. Register in `src/Harbor.Cli/Program.cs` — `builder.AddTool<YourTool>()`.
4. Add tests in `tests/Harbor.Tools.Builtin.Tests/ToolTests.cs`.
5. `dotnet build && dotnet test`.

See [CLAUDE.md](../CLAUDE.md) for code example.

### Add a new LLM provider (JSON-only)

Create `providers/<name>.json` and set env var. Done.

### Add a new test

```csharp
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.YourNamespace.Tests;

public class YourTests
{
    [Test]
    public async Task Method_State_ExpectedResult()
    {
        var result = SomeMethod();
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo("expected");
    }
}
```

### Add a new project

1. Create `src/Harbor.<Name>/` directory.
2. Create `.csproj` file referencing `Harbor.Abstractions` (and `Harbor.Core` if needed).
3. Add `GlobalUsings.cs` if helpful.
4. `dotnet sln add src/Harbor.<Name>/Harbor.<Name>.csproj`.
5. Create corresponding test project in `tests/Harbor.<Name>.Tests/`.

## Code style

Enforced by `.editorconfig` and analyzers (`Roslynator`, `SonarAnalyzer`).

- 4-space indentation.
- File-scoped namespaces.
- `var` only when type is obvious.
- `async`/`await` everywhere, no `.Result`/`.Wait()`.
- `ConfigureAwait(false)` in library code.
- XML doc comments on public APIs.
- Treat warnings as errors.

See [CLAUDE.md](../CLAUDE.md) for full conventions.

## Debugging

### In VS Code

```bash
# Launch config (.vscode/launch.json)
{
  "version": "0.2.0",
  "configurations": [
    {
      "name": "Harbor CLI",
      "type": "coreclr",
      "request": "launch",
      "preLaunchTask": "build",
      "program": "${workspaceFolder}/src/Harbor.Cli/bin/Debug/net10.0/Harbor.Cli.dll",
      "args": ["ask", "Hello"],
      "cwd": "${workspaceFolder}",
      "console": "internalConsole",
      "stopAtEntry": false,
      "env": {
        "ANTHROPIC_API_KEY": "sk-ant-..."
      }
    }
  ]
}
```

### Print debugging

For console output in tests:
```csharp
TestContext.Current.TestOutputWriter.WriteLine($"Debug: {value}");
```

For console output in CLI (when TUI is not active):
```csharp
Console.Error.WriteLine($"Debug: {value}");
```

## Release process

(Planned — not yet implemented)

1. Update version in `Directory.Build.props`.
2. Update `CHANGELOG.md`.
3. Tag: `git tag v0.X.0 && git push --tags`.
4. CI builds NativeAOT binaries for all RIDs.
5. Publish to NuGet as `dotnet tool install harbor`.

## Performance profiling

```bash
# Build Release
dotnet build -c Release

# Run with dotnet-counters
dotnet tool install -g dotnet-counters
dotnet-counters monitor -n harbor --counters System.Runtime

# Heap dump
dotnet tool install -g dotnet-gcdump
dotnet-gcdump collect -n harbor
```

## Troubleshooting

### Build fails with NU1603

Package version mismatch. Run `dotnet restore --force` and check `Directory.Build.props`.

### Tests fail with timeout

Check if `GetScrollback_ReturnsRecentEvents` is hanging — it's skipped by default due to blocking channel read. Other tests should be fast (<1s each).

### Provider not found

- Check `providers/<name>.json` exists.
- Check `id` field is lowercase alphanumeric.
- Run `dotnet run --project src/Harbor.Cli -- providers` to see what's loaded.

### API key not found

- Check env var name matches `authEnvVar` in provider config.
- Conventional names: `ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `OPENROUTER_API_KEY`, etc.
- Run `echo $ANTHROPIC_API_KEY` to verify.

## Contributing

1. Fork the repo.
2. Create a branch: `git checkout -b feature/my-feature`.
3. Make changes following [CLAUDE.md](../CLAUDE.md) conventions.
4. `dotnet build && dotnet test` — must build with 0 warnings in `src/`. The test suite is
   currently RED (469 passed / 10 failed / 1 skipped); acknowledge pre-existing failures.
5. Commit with conventional commits: `feat: add X`, `fix: Y`, `docs: Z`.
6. Open a PR.

## License

MIT — see [LICENSE](../LICENSE).
