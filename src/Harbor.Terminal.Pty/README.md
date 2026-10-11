# Harbor.Terminal.Pty

A pseudo-terminal (PTY) host: it starts a child process inside a real
pseudo-terminal and hands the caller the master side, so interactive shells and
full-screen CLI tools behave the way they do in a normal terminal. This is what
backs the desktop app's floating terminal panes.

Built on `posix_openpt` + `posix_spawn`. **Unix only — Linux and macOS.**
Windows ConPTY is a documented follow-up, not an omission.

## What it is

A low-level library: it owns a PTY master file descriptor and a child process,
and exposes raw bytes. It performs no terminal emulation and no UI.

Since #672 it carries one Harbor reference, `Harbor.Abstractions` (Domain) —
for `ITerminalPaneLauncher` and `PermissionRuleset`. That direction is the
normal one for Infrastructure, and it is what lets the permission gate live
next to the spawn it guards. Application cannot hold the implementation
instead: Application sits a layer *above* this assembly.

## Public API

### `PermissionGatedTerminalPaneLauncher` (the seam, #672)

```csharp
public sealed class PermissionGatedTerminalPaneLauncher : ITerminalPaneLauncher
{
    public const string PanePermission = "terminal";

    public PermissionGatedTerminalPaneLauncher(PermissionRuleset ruleset);

    public Result<ITerminalPane> Launch(
        TerminalPaneRequest request,
        CancellationToken cancellationToken = default);
}
```

**Every launch is gated.** The launcher consults a `PermissionRuleset` and
refuses unless `terminal` evaluates to `Allow`; a refusal is a
`Result.Failure` whose text the UI renders verbatim, and no process is started.
The ruleset is a required constructor argument on purpose — a launcher that had
to guess would guess "allowed", which is the defect #672 exists to remove.

Two rulesets are consulted, and the order matters:

1. **`bash` — denial is final.** Checked first, so the message names the
   stronger reason. A `Deny` under `bash` (including the command-shape guard,
   which denies destructive commands before any rule walk) refuses the pane no
   matter what the pane's own rule says. This closes the footgun where
   "deny `bash`" fenced the agent out while the desktop app kept a shell.
2. **`terminal` — must be explicitly allowed.** The pane's own knob.

The permission is deliberately **not** spelled `bash`. `BashSafetyPolicy`
allows a command only when the whole argv matches an allow rule token-for-token,
which suits one-shot command lines and not a program launch: under `bash` the
pane could only be enabled by naming the exact shell path, e.g.
`/bin/zsh -i`. A dedicated name with no safety policy attached means
`{"terminal": {"*": "allow"}}` is honoured predictably.

`PermissionRuleset.Default` has no `terminal` rule, and an unmatched permission
evaluates to `Ask`, which the launcher treats as a refusal — so **the pane is
denied until an operator opts in**. That is the safe direction: it launches an
interactive shell carrying the full inherited environment, and until #672 it
launched with no gate at all.

### `PtyProcess`

```csharp
public sealed class PtyProcess : IAsyncDisposable
{
    public static bool IsSupported { get; }        // Linux || macOS
    public static Result<PtyProcess> TryStart(PtyStartSpec spec);
    public static PtyProcess Start(PtyStartSpec spec);

    public int Pid { get; }
    public bool HasExited { get; }
    public Task<int> WaitForExitAsync(CancellationToken ct = default);

    public event EventHandler<PtyOutputEventArgs>? OutputReceived;  // raw chunks
    public event EventHandler? OutputClosed;                         // child EOF

    public void Write(byte[] bytes);
    public void Write(string text);
    public void WriteLine(string text);
    public void Resize(int cols, int rows);
    public ValueTask DisposeAsync();
}
```

- `TryStart` is the **ROP boundary** (§E2, #204): it returns
  `Result.Failure<PtyProcess>` with the `errno` in the reason for each failure
  step — `posix_openpt`, `grantpt`/`unlockpt`, slave open, initial resize, and
  `posix_spawn`. `Start` is the throwing convenience wrapper over it.
- Output arrives as **raw bytes** on `OutputReceived` (a reader thread, 8 KiB
  chunks) — this project does no terminal emulation. Emulation and UTF-8
  decoding live in the consumer.
- `Resize` issues `TIOCSWINSZ` on the master.
- `PtyOutputEventArgs(byte[] data)` carries each chunk.

### `PtyStartSpec`

```csharp
public sealed record PtyStartSpec(
    string FileName,
    IReadOnlyList<string>? Args = null,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? ExtraEnvironment = null,
    int Cols = 120,
    int Rows = 32,
    bool SearchPath = true);
```

`SearchPath: true` resolves `FileName` through `PATH` via `posix_spawnp`; set it
to `false` to require an absolute path.

`NativeMethods` is `internal` — the `LibraryImport("libc")` surface
(`posix_openpt`, `grantpt`, `unlockpt`, `ptsname_r`, `open`, `close`, `ioctl`,
`waitpid`, `posix_spawn`) with the Darwin-vs-Linux constant forks
(`O_NOCTTY`, `O_CLOEXEC`, `TIOCSWINSZ`, `POSIX_SPAWN_SETSID`).

## Wiring

There is **no DI module and no `HARBOR_*` env var** — the desktop app
constructs this directly. Its single production consumer is
[`apps/Harbor.App.Avalonia`](../../apps/Harbor.App.Avalonia):

```csharp
services.AddSingleton<ITerminalPaneLauncher>(sp => new PermissionGatedTerminalPaneLauncher(
    PermissionRuleset.Default));
```

**The composition root is the only place that may choose the ruleset.** That is
the point of #672: the policy is one greppable line in the container rather than
a side effect buried in a ViewModel constructor.

`TerminalPaneViewModel` receives `ITerminalPaneLauncher` and calls
`TerminalPaneRequest.InteractiveShell(WorkingDirectory, Title)`. It no longer
names `PtyProcess` at all — it cannot fork, and there is nothing to fake behind.
`FloatingTerminalViewModel` composes panes over that.

The default shell is still `$SHELL`, falling back to `/bin/sh`, resolved by the
ViewModel: reading the environment is a presentation concern, so
`TerminalPaneRequest.InteractiveShell` takes the executable as an argument
rather than looking it up itself.

Unsupported platforms need no special case in the ViewModel any more — the
launcher reports `unsupported-platform` as a `Result.Failure` and the pane
renders it, alongside permission denials and bad shell paths, through the same
branch.

## Usage

```csharp
using Harbor.Terminal.Pty;

if (!PtyProcess.IsSupported)
    return;   // Windows: ConPTY is a follow-up

Result<PtyProcess> started = PtyProcess.TryStart(new PtyStartSpec(
    "/bin/bash",
    Args: ["-i"],
    WorkingDirectory: "/repo",
    Cols: 120,
    Rows: 32));

if (started.IsFailure)
{
    logger.LogWarning("PTY start failed: {Reason}", started.Error);
    return;
}

await using PtyProcess pty = started.Value;
pty.OutputReceived += (_, e) => Console.Out.Write(e.Data);
pty.Resize(cols: 100, rows: 40);
pty.WriteLine("dotnet build");
int exit = await pty.WaitForExitAsync(ct);
```

## Dependencies

One Harbor project reference, `Harbor.Abstractions` (Domain), added in #672 for
`ITerminalPaneLauncher` and `PermissionRuleset`. Package references:
`CSharpFunctionalExtensions` for the `Result` boundaries.

Referenced by `apps/Harbor.App.Avalonia`, `tests/Harbor.Terminal.Pty.Tests` and
`tests/Harbor.Architecture.Tests`.

The edge points inward only. This assembly is Infrastructure and Application
lives above it, so the gated launcher cannot be implemented in Application
without either inverting that direction or forking a second spawn path — the
duplication #672 removes.

## Known limitations

- **No Windows support.** `IsSupported` is `false` and ConPTY is unimplemented.
  The launcher reports this as a `Result.Failure` rather than throwing, so
  callers get a message instead of a P/Invoke load fault; code using `PtyProcess`
  directly must still check `IsSupported` first.
- **No terminal emulation.** You get raw bytes; grid rendering, alternate-screen
  handling, bracketed paste and colour parsing are the consumer's job. This is
  why the desktop panes pair it with a rendering layer.
- **No `bash` builtin execution, job control or signal forwarding.** Ctrl+C is
  delivered by the child only if the pty is in the child's foreground process
  group; Harbor does not manage that.
- **`Read` direction is fire-and-forget.** `OutputReceived` runs on a long-lived
  reader thread; a slow handler applies backpressure to the pipe but there is no
  flow-control API.
- **Synchronous `DisposeAsync` sites must not be assumed safe.** `PtyProcess` is
  `IAsyncDisposable`; the desktop app bridges this explicitly at window/pane
  close.
- Not marked `IsAotCompatible` — the `[LibraryImport]` marshalling is
  source-generated and trim/AOT-safe (#1243); the compat marking itself is
  still a follow-up.

## See also

- [`../../apps/Harbor.App.Avalonia`](../../apps/Harbor.App.Avalonia) — the consuming desktop app
- [`../Harbor.Desktop.Shared/README.md`](../Harbor.Desktop.Shared/README.md) — shared desktop support
- [`../Harbor.Tui.CellForge.Engine/README.md`](../Harbor.Tui.CellForge.Engine/README.md) — the terminal input/emulation layer for the TUI side
