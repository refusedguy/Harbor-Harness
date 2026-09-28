# Harbor.Terminal.Pty

A pseudo-terminal (PTY) host: it starts a child process inside a real
pseudo-terminal and hands the caller the master side, so interactive shells and
full-screen CLI tools behave the way they do in a normal terminal. This is what
backs the desktop app's floating terminal panes.

Built on `posix_openpt` + `posix_spawn`. **Unix only — Linux and macOS.**
Windows ConPTY is a documented follow-up, not an omission.

## What it is

A **leaf library with no Harbor project references at all** — the lowest layer
in the tree. It owns a PTY master file descriptor and a child process, and
exposes raw bytes. It performs no terminal emulation and no UI.

## Public API

Two files, one public type each.

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

`NativeMethods` is `internal` — the `DllImport("libc")` surface
(`posix_openpt`, `grantpt`, `unlockpt`, `ptsname_r`, `open`, `close`, `ioctl`,
`waitpid`, `posix_spawn`) with the Darwin-vs-Linux constant forks
(`O_NOCTTY`, `O_CLOEXEC`, `TIOCSWINSZ`, `POSIX_SPAWN_SETSID`).

## Wiring

There is **no DI module and no `HARBOR_*` env var** — this is a plain library
that the desktop app constructs directly. Its single production consumer is
[`apps/Harbor.App.Avalonia`](../../apps/Harbor.App.Avalonia), in the terminal
pane view models:

- `TerminalPaneViewModel` — checks `PtyProcess.IsSupported` first and, on an
  unsupported platform, closes the pane with an explanatory message instead of
  throwing; then `PtyProcess.Start(new PtyStartSpec(Title, Args: ["-i"], WorkingDirectory: …))`.
  The default shell is `$SHELL`, falling back to `/bin/sh`.
- `FloatingTerminalViewModel` — the same over a floating pane.

Note the defensive shape there: `Start` is used inside a `try`, because the
throwing variant is the convenient one at a call site that already guards on
`IsSupported`.

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

**No Harbor project references at all** — this is a leaf. Its only package
reference is `CSharpFunctionalExtensions` for the `Result` start boundary.

Referenced by `apps/Harbor.App.Avalonia` and `tests/Harbor.Terminal.Pty.Tests`.
The absence of a `Harbor.*` edge is what keeps it usable from any host.

## See also

- [`../../apps/Harbor.App.Avalonia`](../../apps/Harbor.App.Avalonia) — the consuming desktop app
- [`../Harbor.Desktop.Shared/README.md`](../Harbor.Desktop.Shared/README.md) — shared desktop support
- [`../Harbor.Tui.CellForge.Engine/README.md`](../Harbor.Tui.CellForge.Engine/README.md) — the terminal input/emulation layer for the TUI side
