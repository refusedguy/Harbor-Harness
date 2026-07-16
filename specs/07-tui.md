# 07 — TUI (терминальный UI)

> Документ: streaming rendering, slash-commands, split-panes, ANSI, ConsoleAppFramework integration. Custom ANSI wrapper вместо Spectre.Console.Cli. Streaming markdown rendering.

## 1. Цели

1. **Real-time streaming** — токены появляются на экране по мере прихода от LLM, latency <20ms.
2. **Non-blocking input** — пользователь может печатать следующий промпт пока идёт стриминг.
3. **Slash-commands** — `/clear`, `/model`, `/session`, `/help`, и т.д.
4. **Split panes** (опционально) — chat history + editor + status bar.
5. **Cross-platform** — Linux, macOS, Windows (Windows Terminal, WezTerm, iTerm2).
6. **AOT-compatible** — без reflection, без Spectre.Console.Cli.
7. **Low memory** — TUI render loop не должен жрать >20 МБ.

## 2. Архитектура

### 2.1. Single-threaded render loop (Elm-стиль)

Как у crush (charmbracelet) — `UI model` с огромным `switch msg`, обрабатывающим все события. Это **намеренный anti-pattern** относительно textbook Elm (где sub-components имеют свой `update`), но он работает для 5K+ LOC UI кода.

```csharp
public sealed class TuiApp
{
    private readonly Channel<object> _eventChannel = 
        Channel.CreateUnbounded<object>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    
    private TuiState _state = new();
    private readonly TuiRenderer _renderer = new();
    
    public async Task RunAsync(CancellationToken ct)
    {
        await Terminal.EnterRawModeAsync();
        Terminal.HideCursor();
        
        // Initial render
        _renderer.Render(_state);
        
        // Start input listener (writes to channel)
        _ = Task.Run(() => ListenInputAsync(_eventChannel.Writer, ct), ct);
        
        // Main loop
        try
        {
            await foreach (var msg in _eventChannel.Reader.ReadAllAsync(ct))
            {
                _state = Update(_state, msg);
                _renderer.Render(_state);
            }
        }
        finally
        {
            Terminal.ShowCursor();
            await Terminal.ExitRawModeAsync();
        }
    }
    
    private TuiState Update(TuiState state, object msg)
    {
        return msg switch
        {
            KeyPressEvent kp => HandleKey(state, kp),
            TextDeltaEvent td => state with { StreamingText = state.StreamingText + td.Delta },
            ToolCallStartEvent tc => state with { ToolCallStatus = $"Running {tc.ToolName}..." },
            StepFinishEvent sf => state with { TotalTokens = state.TotalTokens + sf.Usage.OutputTokens },
            AgentEndEvent => state with { StreamingText = "", IsBusy = false },
            // ... etc
            _ => state
        };
    }
    
    private TuiState HandleKey(TuiState state, KeyPressEvent kp)
    {
        if (state.IsBusy)
        {
            // During streaming: only Escape (cancel), Ctrl+C, paste
            if (kp.Key == ConsoleKey.Escape) return state with { IsCancelled = true };
            return state;
        }
        
        // Normal input mode
        if (kp.Key == ConsoleKey.Enter && state.InputBuffer.Length > 0)
        {
            var input = state.InputBuffer.ToString();
            if (input.StartsWith('/'))
                return HandleSlashCommand(state, input);
            
            // Submit prompt
            _agent.PromptAsync(input);  // fire-and-forget
            return state with { InputBuffer = new StringBuilder(), IsBusy = true };
        }
        
        if (kp.Key == ConsoleKey.Backspace && state.InputBuffer.Length > 0)
        {
            state.InputBuffer.Length--;
            return state;
        }
        
        if (!char.IsControl(kp.Character))
        {
            state.InputBuffer.Append(kp.Character);
            return state;
        }
        
        return state;
    }
}
```

### 2.2. State

```csharp
public sealed record TuiState
{
    public StringBuilder InputBuffer { get; init; } = new();
    public string StreamingText { get; init; } = "";
    public string StreamingThinking { get; init; } = "";
    public string ToolCallStatus { get; init; } = "";
    public bool IsBusy { get; init; } = false;
    public bool IsCancelled { get; init; } = false;
    public int TotalTokens { get; init; } = 0;
    public decimal TotalCost { get; init; } = 0;
    public string CurrentModel { get; init; } = "";
    public string CurrentAgent { get; init; } = "code";
    public string SessionTitle { get; init; } = "";
    public IReadOnlyList<ChatMessageView> ChatHistory { get; init; } = Array.Empty<ChatMessageView>();
    public string StatusMessage { get; init; } = "";
}

public sealed record ChatMessageView(
    string Role,        // "user" | "assistant" | "tool_result"
    string Content,
    DateTimeOffset Timestamp,
    string? ToolName,
    bool IsError);
```

## 3. ANSI escape codes helper

Полностью AOT-safe (нуль reflection, нуль зависимостей):

```csharp
// Harbor.Tui/Ansi.cs

using System.Text;

namespace Harbor.Tui;

public static class Ansi
{
    // ── Reset / decoration ──
    public const string Reset      = "\x1b[0m";
    public const string Bold       = "\x1b[1m";
    public const string Dim        = "\x1b[2m";
    public const string Italic     = "\x1b[3m";
    public const string Underline  = "\x1b[4m";
    public const string Blink      = "\x1b[5m";
    public const string Reverse    = "\x1b[7m";
    public const string Hidden     = "\x1b[8m";
    public const string Strike     = "\x1b[9m";
    
    // ── Foreground (16-color) ──
    public const string Black    = "\x1b[30m";
    public const string Red      = "\x1b[31m";
    public const string Green    = "\x1b[32m";
    public const string Yellow   = "\x1b[33m";
    public const string Blue     = "\x1b[34m";
    public const string Magenta  = "\x1b[35m";
    public const string Cyan     = "\x1b[36m";
    public const string White    = "\x1b[37m";
    public const string BrightBlack   = "\x1b[90m";
    public const string BrightRed     = "\x1b[91m";
    public const string BrightGreen   = "\x1b[92m";
    public const string BrightYellow  = "\x1b[93m";
    public const string BrightBlue    = "\x1b[94m";
    public const string BrightMagenta = "\x1b[95m";
    public const string BrightCyan    = "\x1b[96m";
    public const string BrightWhite   = "\x1b[97m";
    
    // ── Background (16-color) ──
    public const string BgBlack    = "\x1b[40m";
    public const string BgRed      = "\x1b[41m";
    // ... etc
    
    // ── 256-color ──
    public static string Fg(int n) => $"\x1b[38;5;{n}m";
    public static string Bg(int n) => $"\x1b[48;5;{n}m";
    
    // ── TrueColor ──
    public static string Fg(int r, int g, int b) => $"\x1b[38;2;{r};{g};{b}m";
    public static string Bg(int r, int g, int b) => $"\x1b[48;2;{r};{g};{b}m";
    
    // ── Cursor ──
    public static void MoveTo(int row, int col) => Console.Write($"\x1b[{row};{col}H");
    public static void MoveUp(int n) => Console.Write($"\x1b[{n}A");
    public static void MoveDown(int n) => Console.Write($"\x1b[{n}B");
    public static void MoveRight(int n) => Console.Write($"\x1b[{n}C");
    public static void MoveLeft(int n) => Console.Write($"\x1b[{n}D");
    public static void MoveToStartOfLine() => Console.Write("\r");
    public static void ClearLine() => Console.Write("\x1b[2K");
    public static void ClearLineFromCursor() => Console.Write("\x1b[K");
    public static void ClearScreen() => Console.Write("\x1b[2J");
    public static void ClearScreenFromCursor() => Console.Write("\x1b[J");
    public static void SaveCursor() => Console.Write("\x1b[s");
    public static void RestoreCursor() => Console.Write("\x1b[u");
    public static void HideCursor() => Console.Write("\x1b[?25l");
    public static void ShowCursor() => Console.Write("\x1b[?25h");
    
    // ── Screen buffer ──
    public static void EnterAltScreen() => Console.Write("\x1b[?1049h");
    public static void ExitAltScreen() => Console.Write("\x1b[?1049l");
    
    // ── Scrolling ──
    public static void SetScrollRegion(int top, int bottom) => 
        Console.Write($"\x1b[{top};{bottom}r");
    public static void ResetScrollRegion() => Console.Write("\x1b[r");
    public static void ScrollUp(int n) => Console.Write($"\x1b[{n}S");
    public static void ScrollDown(int n) => Console.Write($"\x1b[{n}T");
    
    // ── Helpers ──
    public static void Write(string text) => Console.Write(text);
    public static void WriteLn(string text) => Console.WriteLine(text);
    
    public static void WriteColored(string text, string fg, string bg = "")
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(fg)) sb.Append(fg);
        if (!string.IsNullOrEmpty(bg)) sb.Append(bg);
        sb.Append(text);
        sb.Append(Reset);
        Console.Write(sb.ToString());
    }
    
    public static void WriteAt(int row, int col, string text)
    {
        SaveCursor();
        MoveTo(row, col);
        Console.Write(text);
        RestoreCursor();
    }
}

// Terminal mode management
public static class Terminal
{
    private static TerminalMode? _originalMode;
    
    public static async Task EnterRawModeAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows: use Win32 API to disable line input and echo
            var handle = GetStdHandle(STD_INPUT_HANDLE);
            GetConsoleMode(handle, out var mode);
            _originalMode = new TerminalMode(mode);
            var newMode = mode & ~(ENABLE_ECHO_INPUT | ENABLE_LINE_INPUT | ENABLE_PROCESSED_INPUT);
            SetConsoleMode(handle, newMode);
        }
        else
        {
            // Unix: use termios
            _ = await RunTermiosAsync("-raw");
        }
        
        // Disable stdout buffering for immediate output
        Console.Out.Flush();
    }
    
    public static async Task ExitRawModeAsync()
    {
        if (_originalMode == null) return;
        
        if (OperatingSystem.IsWindows())
        {
            var handle = GetStdHandle(STD_INPUT_HANDLE);
            SetConsoleMode(handle, _originalMode.ConsoleMode);
        }
        else
        {
            _ = await RunTermiosAsync("raw");  // restore
        }
    }
    
    [DllImport("kernel32")] private static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32")] private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
    [DllImport("kernel32")] private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
    
    private const int STD_INPUT_HANDLE = -10;
    private const uint ENABLE_ECHO_INPUT = 0x0004;
    private const uint ENABLE_LINE_INPUT = 0x0002;
    private const uint ENABLE_PROCESSED_INPUT = 0x0001;
    
    private static async Task<string> RunTermiosAsync(string args)
    {
        var psi = new ProcessStartInfo("stty", args) { RedirectStandardOutput = true };
        using var p = Process.Start(psi)!;
        return await p.StandardOutput.ReadToEndAsync();
    }
}

public sealed class TerminalMode(uint consoleMode)
{
    public uint ConsoleMode { get; } = consoleMode;
}
```

## 4. Streaming markdown rendering (prefix cache)

Главная инновация crush — **prefix cache** для streaming markdown. Проблема: во время стриминга LLM отдаёт markdown по токенам, а re-рендер всего markdown на каждый токен = O(n²). Решение: кэшировать рендер stable prefix'а, ре-рендерить только trailing partial.

```csharp
public sealed class StreamingMarkdownRenderer
{
    private string _lastRenderedText = "";
    private string _lastRenderedHtml = "";
    private int _stablePrefixLength = 0;
    
    /// <summary>Update text and return what to write to console.</summary>
    public string Update(string newText)
    {
        // Find longest stable prefix (where new text matches last rendered)
        var commonPrefix = FindCommonPrefix(_lastRenderedText, newText);
        
        // Find safe boundary — last markdown element boundary before commonPrefix
        var safeBoundary = FindSafeMarkdownBoundary(newText, Math.Min(commonPrefix, _stablePrefixLength));
        
        // Re-render from safeBoundary onwards
        var stablePart = newText[..safeBoundary];
        var unstablePart = newText[safePrefixLength..];
        
        var stableRendered = _lastRenderedHtml[..FindRenderedBoundary(_lastRenderedText, safeBoundary)];
        var unstableRendered = RenderMarkdown(unstablePart);
        
        _lastRenderedText = newText;
        _lastRenderedHtml = stableRendered + unstableRendered;
        _stablePrefixLength = safeBoundary;
        
        // Console output: clear from safe boundary, write unstable part
        return unstableRendered;
    }
    
    private static int FindCommonPrefix(string a, string b)
    {
        var min = Math.Min(a.Length, b.Length);
        for (int i = 0; i < min; i++)
            if (a[i] != b[i]) return i;
        return min;
    }
    
    /// <summary>Find last position where we can safely split markdown without breaking syntax.</summary>
    private static int FindSafeMarkdownBoundary(string text, int maxLength)
    {
        // Walk backwards from maxLength, find boundary:
        // - end of code block (```)
        // - end of paragraph (\n\n)
        // - end of line outside inline formatting
        
        for (int i = Math.Min(maxLength, text.Length - 1); i > 0; i--)
        {
            // Check we're not in the middle of a code block
            if (IsInsideCodeBlock(text, i)) continue;
            
            // Check we're not in the middle of an inline code span
            if (IsInsideInlineCode(text, i)) continue;
            
            // Prefer paragraph boundaries
            if (i >= 2 && text[i-1] == '\n' && text[i-2] == '\n')
                return i;
            
            // Line boundaries
            if (text[i-1] == '\n' && !IsInsideList(text, i))
                return i;
        }
        
        return 0;
    }
    
    private static bool IsInsideCodeBlock(string text, int pos)
    {
        // Count ``` occurrences before pos — if odd, we're inside code block
        var count = 0;
        for (int i = 0; i + 2 < pos; i++)
            if (text[i] == '`' && text[i+1] == '`' && text[i+2] == '`')
                count++;
        return count % 2 == 1;
    }
    
    // ... etc
    
    private static string RenderMarkdown(string text)
    {
        // Use Markdig with ANSI renderer
        var pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .Build();
        var doc = Markdown.Parse(text, pipeline);
        
        var renderer = new AnsiMarkdownRenderer(new AnsiRendererOptions
        {
            HeadingColor = Ansi.Cyan,
            LinkColor = Ansi.Blue + Ansi.Underline,
            CodeBlockColor = Ansi.Dim,
            InlineCodeColor = Ansi.Yellow,
            BlockquoteColor = Ansi.Italic + Ansi.Dim,
            ListBulletColor = Ansi.BrightYellow
        });
        renderer.Render(doc);
        return renderer.ToString();
    }
}
```

Для MVP можно обойтись **без** prefix cache (просто re-render всего markdown на каждый delta, но с throttling 30fps). Prefix cache — v1.

## 5. Layout (single-pane MVP)

Простейший layout для MVP — без split-panes, всё в одном column:

```
┌──────────────────────────────────────────────────────────────────────────┐
│ Harbor v0.1.0  |  claude-opus-4  |  agent: code  |  $0.0123  |  1.2K tok │ <- status bar (1 line)
├──────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  [user] fix the login bug in src/auth.ts                                 │
│                                                                          │
│  [assistant] I'll investigate the login implementation first.            │
│  → read src/auth.ts                                                      │
│                                                                          │
│  I see the issue — the password comparison uses `==` instead of          │
│  `===`. Let me fix that.                                                 │
│  → edit src/auth.ts (oldString: `password == userInput`, newString: ...) │
│                                                                          │
│  Done. The fix:                                                          │
│  ```typescript                                                            │
│  if (userInput === password) {  // was ==                                 │
│    // ...                                                                 │
│  }                                                                        │
│  ```                                                                      │
│                                                                          │
│  [user] great, also add a test                                           │
│                                                                          │
│  [assistant] ▊  ← (cursor while streaming)                                │
│                                                                          │
├──────────────────────────────────────────────────────────────────────────┤
│ > add a test for the password check_  ← (input line, cursor at _)         │
└──────────────────────────────────────────────────────────────────────────┘
```

Layout алгоритм (псевдокод):
1. Reserve 1 line top for status bar.
2. Reserve 2 lines bottom for input + hint.
3. Middle = chat history (scrollable).
4. Chat history auto-scrolls to bottom when new content arrives.

```csharp
public sealed class SimpleLayout
{
    public void Render(TuiState state, int consoleWidth, int consoleHeight)
    {
        // 1. Clear screen (or use diff)
        Ansi.ClearScreen();
        
        // 2. Render status bar (top 1 line)
        Ansi.MoveTo(1, 1);
        Ansi.WriteColored(
            $" Harbor v{HarborVersion.Current}  |  {state.CurrentModel}  |  agent: {state.CurrentAgent}  |  ${state.TotalCost:F4}  |  {state.TotalTokens} tok",
            fg: Ansi.BrightBlack,
            bg: "");
        
        // 3. Render chat history (middle)
        var historyHeight = consoleHeight - 4;  // status + 2 input
        var historyLines = RenderHistory(state, consoleWidth, historyHeight);
        
        for (int i = 0; i < historyLines.Count && i < historyHeight; i++)
        {
            Ansi.MoveTo(i + 2, 1);
            Console.Write(historyLines[i]);
        }
        
        // 4. Render separator
        Ansi.MoveTo(consoleHeight - 1, 1);
        Ansi.WriteColored(new string('─', consoleWidth), Ansi.BrightBlack, "");
        
        // 5. Render input line
        Ansi.MoveTo(consoleHeight, 1);
        Ansi.WriteColored("> ", Ansi.Green, "");
        Console.Write(state.InputBuffer.ToString());
        if (state.IsBusy) Ansi.WriteColored(" ▊", Ansi.BrightBlack, "");  // cursor block
    }
    
    private IReadOnlyList<string> RenderHistory(TuiState state, int width, int maxHeight)
    {
        var lines = new List<string>();
        
        foreach (var msg in state.ChatHistory)
        {
            var roleLabel = msg.Role switch
            {
                "user" => Ansi.Green + "[user]" + Ansi.Reset,
                "assistant" => Ansi.Cyan + "[assistant]" + Ansi.Reset,
                "tool_result" => Ansi.Dim + $"→ {msg.ToolName}" + Ansi.Reset,
                _ => msg.Role
            };
            
            // Wrap text at width - 4 (indentation)
            var wrappedLines = WordWrap(msg.Content, width - 4);
            
            lines.Add($"{roleLabel} {wrappedLines[0]}");
            for (int i = 1; i < wrappedLines.Count; i++)
                lines.Add($"    {wrappedLines[i]}");
            lines.Add("");  // blank line between messages
        }
        
        // Streaming content
        if (!string.IsNullOrEmpty(state.StreamingText))
        {
            lines.Add($"{Ansi.Cyan}[assistant]{Ansi.Reset} {state.StreamingText}");
        }
        
        if (!string.IsNullOrEmpty(state.ToolCallStatus))
        {
            lines.Add($"{Ansi.Dim}{state.ToolCallStatus}{Ansi.Reset}");
        }
        
        // Take last N lines (scroll to bottom)
        if (lines.Count > maxHeight)
            return lines.Skip(lines.Count - maxHeight).ToList();
        
        return lines;
    }
    
    private IReadOnlyList<string> WordWrap(string text, int width)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            var words = paragraph.Split(' ');
            var current = new StringBuilder();
            foreach (var word in words)
            {
                if (current.Length + word.Length + 1 > width)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                }
                if (current.Length > 0) current.Append(' ');
                current.Append(word);
            }
            lines.Add(current.ToString());
        }
        return lines;
    }
}
```

## 6. Diff rendering для edit tool

```csharp
public sealed class DiffRenderer
{
    public string RenderDiff(string oldText, string newText)
    {
        var diff = InlineDiffBuilder.Diff(oldText, newText);  // DiffPlex
        var sb = new StringBuilder();
        
        foreach (var line in diff.Lines)
        {
            switch (line.Type)
            {
                case ChangeType.Unchanged:
                    sb.AppendLine($" {line.Text}");
                    break;
                case ChangeType.Deleted:
                    sb.AppendLine($"{Ansi.Red}-{line.Text}{Ansi.Reset}");
                    break;
                case ChangeType.Inserted:
                    sb.AppendLine($"{Ansi.Green}+{line.Text}{Ansi.Reset}");
                    break;
                case ChangeType.Modified:
                    sb.AppendLine($"{Ansi.Yellow}~{line.Text}{Ansi.Reset}");
                    break;
            }
        }
        
        return sb.ToString();
    }
}
```

## 7. Slash-commands

### 7.1. Command router

```csharp
public interface ISlashCommand
{
    string Name { get; }   // "clear", "model", etc. (without leading /)
    string Description { get; }
    string Usage { get; }
    IReadOnlyList<string> Aliases { get; }
    
    Task ExecuteAsync(IReadOnlyList<string> args, ICommandContext ctx, CancellationToken ct);
}

public interface ICommandContext
{
    ISessionContext Session { get; }
    IAgent Agent { get; }
    IProviderRegistry Providers { get; }
    IToolRegistry Tools { get; }
    IConfig Config { get; }
    Action<string> Output { get; }  // for command output (printed to TUI)
    Func<string, Task<string>> Prompt { get; }  // for interactive input
}

public sealed class SlashCommandRouter
{
    private readonly Dictionary<string, ISlashCommand> _commands = new(StringComparer.OrdinalIgnoreCase);
    
    public void Register(ISlashCommand command)
    {
        _commands[command.Name] = command;
        foreach (var alias in command.Aliases)
            _commands[alias] = command;
    }
    
    public async Task<bool> TryHandleAsync(string input, ICommandContext ctx, CancellationToken ct)
    {
        if (!input.StartsWith('/')) return false;
        
        var parts = input[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        
        if (!_commands.TryGetValue(parts[0], out var command))
        {
            ctx.Output($"Unknown command: /{parts[0].Type} /help for available commands.");
            return true;
        }
        
        try
        {
            await command.ExecuteAsync(parts.Skip(1).ToList(), ctx, ct);
        }
        catch (Exception ex)
        {
            ctx.Output($"Command failed: {ex.Message}");
        }
        
        return true;
    }
}
```

### 7.2. Builtin slash-commands

| Command | Aliases | Description |
|---|---|---|
| `/help` | `/?` | List available commands |
| `/clear` | | Clear chat history (start new session) |
| `/model <name>` | `/m` | Switch model (e.g., `/model claude-opus-4`) |
| `/agent <name>` | `/mode` | Switch agent/mode (e.g., `/agent plan`) |
| `/session list` | `/s` | List sessions |
| `/session resume <id>` | | Resume session |
| `/session fork <msg-id>` | | Fork from message |
| `/session export` | | Export current session as JSONL |
| `/session stats` | | Show session stats (tokens, cost, duration) |
| `/compact` | | Manually trigger compaction |
| `/tools` | | List available tools |
| `/tools enable <name>` | | Enable tool |
| `/tools disable <name>` | | Disable tool |
| `/plugins` | | List loaded plugins |
| `/permissions` | `/perm` | Show current permissions |
| `/permissions allow <tool> <pattern>` | | Add allow rule |
| `/permissions deny <tool> <pattern>` | | Add deny rule |
| `/mcp` | | List MCP servers |
| `/mcp connect <name>` | | Connect MCP server |
| `/mcp disconnect <name>` | | Disconnect MCP server |
| `/skills` | | List available skills |
| `/skill <name>` | | Load a skill |
| `/cost` | | Show total cost |
| `/tokens` | | Show total tokens |
| `/revert <msg-id>` | | Revert to message (FS + history) |
| `/config` | | Show/edit config |
| `/auth set <provider> <key>` | | Set API key |
| `/auth login <provider>` | | OAuth login |
| `/auth status` | | Show auth status |
| `/quit` | `/q`, `/exit` | Exit harbor |

### 7.3. Пример команды

```csharp
public sealed class ModelCommand : ISlashCommand
{
    public string Name => "model";
    public string Description => "Switch LLM model";
    public string Usage => "/model <provider/model> | /model list";
    public IReadOnlyList<string> Aliases => new[] { "m" };
    
    public async Task ExecuteAsync(IReadOnlyList<string> args, ICommandContext ctx, CancellationToken ct)
    {
        if (args.Count == 0 || args[0] == "list")
        {
            var models = await ctx.Providers.GetAllModelsAsync(ct);
            ctx.Output("Available models:");
            foreach (var m in models)
            {
                var current = m.Id == ctx.Session.LastModel ? " ← current" : "";
                ctx.Output($"  {m.ProviderId}/{m.Id} — {m.DisplayName} ({m.ContextWindow} ctx){current}");
            }
            return;
        }
        
        var modelArg = string.Join(' ', args);
        var parts = modelArg.Split('/', 2);
        if (parts.Length == 2)
        {
            var providerId = parts[0];
            var modelId = parts[1];
            ctx.Config.Set("model", modelArg);
            ctx.Output($"Switched to {modelArg}");
        }
        else
        {
            // Search by partial match
            var models = await ctx.Providers.GetAllModelsAsync(ct);
            var match = models.FirstOrDefault(m => 
                m.Id.Contains(modelArg, StringComparison.OrdinalIgnoreCase) ||
                m.DisplayName.Contains(modelArg, StringComparison.OrdinalIgnoreCase));
            
            if (match == null)
            {
                ctx.Output($"Model not found: {modelArg}. Use `/model list` to see available.");
                return;
            }
            
            ctx.Config.Set("model", $"{match.ProviderId}/{match.Id}");
            ctx.Output($"Switched to {match.ProviderId}/{match.Id}");
        }
    }
}
```

## 8. Input handling

### 8.1. Key reading

В raw mode `Console.ReadKey` работает, но возвращает каждый keypress сразу (без Enter):

```csharp
public sealed class InputListener
{
    public async Task ListenAsync(ChannelWriter<object> writer, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            ConsoleKeyInfo key;
            try
            {
                key = Console.ReadKey(intercept: true);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            
            // Handle escape sequences (arrow keys, etc.)
            if (key.Key == ConsoleKey.Escape)
            {
                // Could be escape alone, or escape sequence
                // Read additional bytes if available
                if (Console.KeyAvailable)
                {
                    // Parse escape sequence (e.g., \x1b[A = Up arrow)
                    var seq = new StringBuilder("\x1b");
                    while (Console.KeyAvailable)
                    {
                        var k = Console.ReadKey(intercept: true);
                        seq.Append(k.KeyChar);
                    }
                    writer.TryWrite(new EscapeSequenceEvent(seq.ToString()));
                    continue;
                }
                
                writer.TryWrite(new KeyPressEvent(ConsoleKey.Escape, '\x1b', modifiers: default));
                continue;
            }
            
            // Ctrl+C
            if (key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0)
            {
                writer.TryWrite(new CtrlCEvent());
                continue;
            }
            
            writer.TryWrite(new KeyPressEvent(key.Key, key.KeyChar, key.Modifiers));
        }
    }
}

public sealed record KeyPressEvent(ConsoleKey Key, char Character, ConsoleModifiers Modifiers);
public sealed record EscapeSequenceEvent(string Sequence);
public sealed record CtrlCEvent();
```

### 8.2. Multi-line input

Поддержка многострочного ввода через Shift+Enter (или любой другой модификатор):

```csharp
// В KeyPress handler:
if (key.Key == ConsoleKey.Enter)
{
    if ((key.Modifiers & ConsoleModifiers.Shift) != 0)
    {
        // Newline in input
        state.InputBuffer.Append('\n');
        return state;
    }
    
    // Submit
    if (state.InputBuffer.Length > 0)
    {
        // ... submit prompt
    }
}
```

### 8.3. Bracketed paste mode

Терминалы поддерживают bracketed paste — paste помечается escape-последовательностями `\x1b[200~...\x1b[201~`. Это позволяет отличить paste от обычного ввода:

```csharp
// Enable bracketed paste on startup
Console.Write("\x1b[?2004h");

// In input handler:
private bool _inPaste = false;

if (seq == "\x1b[200~") { _inPaste = true; continue; }
if (seq == "\x1b[201~") { _inPaste = false; continue; }

if (_inPaste)
{
    // Treat as bulk insert — no special handling of newlines
    state.InputBuffer.Append(key.KeyChar);
    continue;
}
```

### 8.4. Line editor (vim/emacs modes)

Полноценный line editor с историей (up/down arrows), word navigation (Ctrl+W, Alt+B/F), yank (Ctrl+Y) — как у crush.

В MVP — минимальный editor: backspace, left/right, up/down для history, Ctrl+W (delete word), Ctrl+U (delete line).

В v1 — vim mode (полная поддержка: normal/insert/visual modes, motions, operators).

## 9. Permission prompt UI

Когда `bash` требует подтверждения — модальный диалог:

```
┌─ Permission Required ──────────────────────────────────────┐
│                                                            │
│  Tool: bash                                                │
│  Command: rm -rf node_modules                             │
│                                                            │
│  [y] Allow once   [a] Always allow                        │
│  [n] Deny once    [N] Always deny                          │
│  [Esc] Cancel                                              │
│                                                            │
└────────────────────────────────────────────────────────────┘
```

Реализация:

```csharp
public sealed class PermissionDialog
{
    public async Task<PermissionResponse> ShowAsync(
        PermissionRequest request,
        CancellationToken ct)
    {
        // Save current state, render dialog
        var savedState = _state;
        _state = _state with { Dialog = new PermissionDialogState(request) };
        _renderer.Render(_state);
        
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var key = await WaitForNextKeyAsync(ct);
                
                return key.Key switch
                {
                    ConsoleKey.Y => new PermissionResponse(PermissionAction.Allow, PersistDecision: false),
                    ConsoleKey.A => new PermissionResponse(PermissionAction.Allow, PersistDecision: true),
                    ConsoleKey.N => new PermissionResponse(PermissionAction.Deny, PersistDecision: false),
                    ConsoleKey.Escape => new PermissionResponse(PermissionAction.Deny, PersistDecision: false),
                    _ => continue  // ignore other keys
                };
            }
            
            return new PermissionResponse(PermissionAction.Deny, PersistDecision: false);
        }
        finally
        {
            _state = savedState;
            _renderer.Render(_state);
        }
    }
}
```

## 10. ConsoleAppFramework integration для CLI

Когда пользователь вызывает `harbor run "prompt"` (one-shot mode, без TUI), используем `ConsoleAppFramework v5` для парсинга аргументов:

```csharp
// Harbor.Cli/Program.cs
using ConsoleAppFramework;

await ConsoleApp.RunAsync(args, App.Run);

static partial class App
{
    /// <summary>
    /// Start interactive TUI session.
    /// </summary>
    /// <param name="model">-m, Model to use (e.g., anthropic/claude-opus-4)</param>
    /// <param name="agent">-a, Agent/mode (code, plan, explore)</param>
    /// <param name="resume">-r, Resume session by ID</param>
    /// <param name="ct">Cancellation token</param>
    public static async Task Run(
        string? model = null,
        string? agent = null,
        string? resume = null,
        CancellationToken ct = default)
    {
        var host = HostBuilder.Build(args);
        var tuiApp = host.Services.GetRequiredService<TuiApp>();
        
        if (model != null) host.Services.GetRequiredService<IConfig>().Set("model", model);
        if (agent != null) host.Services.GetRequiredService<IConfig>().Set("agent", agent);
        if (resume != null) await host.Services.GetRequiredService<ISessionStore>().GetAsync(resume, ct);
        
        await tuiApp.RunAsync(ct);
    }
    
    /// <summary>
    /// Run one-shot prompt without TUI (prints to stdout).
    /// </summary>
    /// <param name="prompt">-p, Prompt text</param>
    /// <param name="model">-m, Model</param>
    /// <param name="ct">Cancellation token</param>
    public static async Task Ask(
        string prompt,
        string? model = null,
        CancellationToken ct = default)
    {
        var host = HostBuilder.Build(args);
        var agent = host.Services.GetRequiredService<IAgent>();
        await agent.PromptAsync(prompt, ct);
        
        await foreach (var evt in agent.Events.ReadAllAsync(ct))
        {
            if (evt is TextDeltaEvent td) Console.Write(td.Delta);
            if (evt is AgentEndEvent) break;
        }
    }
    
    /// <summary>
    /// Start harbor as HTTP server (for client/server mode).
    /// </summary>
    /// <param name="port">-p, Port (default: 4096)</param>
    /// <param name="ct">Cancellation token</param>
    public static async Task Serve(
        int port = 4096,
        CancellationToken ct = default)
    {
        var host = HostBuilder.Build(args);
        await host.Services.GetRequiredService<HttpServer>().StartAsync(port, ct);
    }
    
    /// <summary>Manage plugins.</summary>
    public static async Task Plugin(
        [Argument] string action,  // install | uninstall | list | update
        string? name = null,
        CancellationToken ct = default)
    {
        // ...
    }
    
    /// <summary>Manage sessions.</summary>
    public static async Task Session(
        [Argument] string action,  // list | resume | fork | export | import | delete | stats
        string? id = null,
        CancellationToken ct = default)
    {
        // ...
    }
    
    /// <summary>Manage models.</summary>
    public static async Task Models(
        [Argument] string action = "list",  // list | update
        CancellationToken ct = default)
    {
        // ...
    }
}
```

`ConsoleAppFramework v5` — это **source generator-based** CLI parser, zero reflection, AOT-safe. Генерирует dispatch-код на этапе компиляции:

```csharp
// Generated:
partial class App
{
    static partial void AddCore(string commandName, Delegate command)
    {
        switch (commandName)
        {
            case "run": _runCommand = Unsafe.As<Func<string?, string?, string?, CancellationToken, Task>>(command); break;
            case "ask": _askCommand = /* ... */; break;
            case "serve": /* ... */; break;
            // ...
        }
    }
}
```

## 11. Вывод в non-TUI режимах

### 11.1. Print mode (`harbor ask "prompt"`)

Простой streaming в stdout, без ANSI colors (если не TTY):

```csharp
public sealed class PrintModeRenderer
{
    public async Task RenderAsync(IAsyncEnumerable<AgentEvent> stream, CancellationToken ct)
    {
        var useColor = Console.IsOutputRedirected ? false : true;
        
        await foreach (var evt in stream)
        {
            switch (evt)
            {
                case MessageUpdateEvent mu when mu.LlmEvent is TextDeltaEvent td:
                    Console.Write(td.Delta);
                    break;
                
                case ToolExecutionStartEvent tes:
                    if (useColor) Console.Write($"\n{Ansi.Dim}");
                    Console.Write($"→ {tes.ToolName}");
                    if (useColor) Console.Write(Ansi.Reset);
                    Console.WriteLine();
                    break;
                
                case AgentEndEvent:
                    return;
            }
        }
    }
}
```

### 11.2. JSON mode (`harbor ask --json "prompt"`)

Streaming events как JSONL в stdout (для IDE integration):

```csharp
public sealed class JsonModeRenderer
{
    public async Task RenderAsync(IAsyncEnumerable<AgentEvent> stream, CancellationToken ct)
    {
        await foreach (var evt in stream)
        {
            var json = JsonSerializer.Serialize(evt, HarborEventContext.Default.AgentEvent);
            Console.WriteLine(json);
        }
    }
}
```

```jsonl
{"type":"message_start","message":{"id":"abc","role":"assistant",...}}
{"type":"message_update","llmEvent":{"type":"text_delta","id":"0","delta":"Hello"}}
{"type":"message_update","llmEvent":{"type":"text_delta","id":"0","delta":", "}}
{"type":"message_update","llmEvent":{"type":"text_delta","id":"0","delta":"world!"}}
{"type":"message_end","message":{"id":"abc","role":"assistant","parts":[{"type":"text","text":"Hello, world!"}],...}}
{"type":"agent_end","messages":[...]}
```

## 12. Throttling и debouncing

LLM может выдавать токены быстрее, чем TUI успевает рендерить. Решение — throttling:

```csharp
public sealed class ThrottledRenderer
{
    private readonly TimeSpan _minRenderInterval = TimeSpan.FromMilliseconds(33);  // 30fps
    private DateTimeOffset _lastRender = DateTimeOffset.MinValue;
    private string _pendingText = "";
    
    public void AppendText(string text)
    {
        _pendingText += text;
        
        var now = DateTimeOffset.UtcNow;
        if (now - _lastRender >= _minRenderInterval)
        {
            Flush();
            _lastRender = now;
        }
    }
    
    public void Flush()
    {
        if (_pendingText.Length == 0) return;
        Console.Write(_pendingText);
        _pendingText = "";
    }
}
```

Всегда flush на:
- `MessageEnd` (полный assistant message)
- `ToolCallStart` (новая строка, переход к tool output)
- `AgentEnd` (завершение)

## 13. Performance targets

| Metric | Target | Notes |
|---|---|---|
| Token-to-screen latency | <20 ms | LLM delta → console write |
| Render throughput | 1000 tokens/sec sustained | Without visible lag |
| Input → echo latency | <5 ms | Keypress → character on screen |
| Slash-command execution | <50 ms | Command → result displayed |
| TUI memory overhead | <20 MB | Render buffers, state, etc. |
| Cold start to interactive | <50 ms | `harbor` → ready for input |

## 14. Windows-specific considerations

- `Console.Write` с ANSI codes работает в Windows 10+ только если включен VT processing (через `SetConsoleMode` с `ENABLE_VIRTUAL_TERMINAL_PROCESSING`).
- На старых Windows (7/8) — fallback на `Console.BackgroundColor`/`Console.ForegroundColor`.
- `Console.ReadKey` в raw mode — Windows поддерживает `ENABLE_LINE_INPUT = 0` через Win32 API.
- UTF-8: `Console.OutputEncoding = Encoding.UTF8;` на startup.

```csharp
private static void EnableWindowsAnsi()
{
    if (!OperatingSystem.IsWindows()) return;
    
    var handle = GetStdHandle(STD_OUTPUT_HANDLE);
    GetConsoleMode(handle, out var mode);
    SetConsoleMode(handle, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
    
    Console.OutputEncoding = Encoding.UTF8;
}

[DllImport("kernel32")] private static extern IntPtr GetStdHandle(int n);
[DllImport("kernel32")] private static extern bool GetConsoleMode(IntPtr h, out uint m);
[DllImport("kernel32")] private static extern bool SetConsoleMode(IntPtr h, uint m);

private const int STD_OUTPUT_HANDLE = -11;
private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
```

## 15. Spectre.Console как опциональный рендер

Для rich widgets (таблицы, progress bars, panels) можно использовать `Spectre.Console` ≥ 0.50 (только рендеринг, не CLI):

```csharp
// Опционально, только если нужен rich rendering
public sealed class SpectreRichRenderer
{
    public void RenderToolResultsTable(IReadOnlyList<ToolResult> results)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Tool")
            .AddColumn("Status")
            .AddColumn("Output (truncated)");
        
        foreach (var r in results)
        {
            table.AddRow(
                r.ToolName,
                r.IsError ? "[red]ERROR[/]" : "[green]OK[/]",
                r.Output.Length > 100 ? r.Output[..100] + "..." : r.Output);
        }
        
        AnsiConsole.Write(table);
    }
}
```

В MVP — без Spectre.Console, чистый `Console.Write` + ANSI. Spectre — v1, опционально.

---

**Next**: `08-native-aot.md` — ограничения NativeAOT, reflection, trimming, что НЕ работает.
