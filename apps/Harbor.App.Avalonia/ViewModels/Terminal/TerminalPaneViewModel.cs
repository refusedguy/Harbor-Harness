using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Harbor.Terminal.Pty;
using Harbor.Ui.Framework.Services;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Avalonia.ViewModels.Terminal;

/// <summary>
///     One PTY-backed terminal pane. The child shell runs out-of-process
///     inside a real pseudo-terminal rooted at <see cref="WorkingDirectory" />
///     with the full inherited environment; raw output is decoded
///     incrementally (UTF-8 across chunk boundaries is safe), ANSI control
///     sequences are stripped, and the tail is capped so every pane keeps an
///     independent bounded history.
/// </summary>
public sealed partial class TerminalPaneViewModel : ObservableObject, IDisposable
{
    private const int MaxBufferChars = 200_000;

    private readonly PtyProcess? _pty;
    private readonly IDispatcherAdapter _dispatcher;
    private readonly ILogger _logger;
    private readonly StringBuilder _buffer = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly ReadOnlyCollection<string> _commandHistory = [];
    private int _historyIndex = -1;

    [ObservableProperty]
    private string _outputText = string.Empty;

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private string _title = "shell";

    /// <summary>Timestamped at construction — the directory the shell starts in.</summary>
    public string WorkingDirectory { get; }

    /// <summary>True once the child exited or the PTY closed.</summary>
    [ObservableProperty]
    private bool _isClosed;

    public TerminalPaneViewModel(
        IDispatcherAdapter dispatcher,
        ILogger<TerminalPaneViewModel> logger,
        string? workingDirectory = null,
        string? shell = null)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory;
        Title = shell ?? Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh";

        if (!PtyProcess.IsSupported)
        {
            IsClosed = true;
            OutputText = "PTY sessions are not supported on this platform (Windows ConPTY is a follow-up).";
            return;
        }

        try
        {
            _pty = PtyProcess.Start(new PtyStartSpec(
                Title,
                Args: ["-i"],
                WorkingDirectory: WorkingDirectory));
            _pty.OutputReceived += OnOutputReceived;
            _pty.OutputClosed += OnOutputClosed;
            _logger.LogInformation("PTY pane started: {Shell} pid={Pid} cwd={Cwd}", Title, _pty.Pid, WorkingDirectory);
        }
        catch (Exception ex)
        {
            IsClosed = true;
            OutputText = $"Failed to start terminal: {ex.Message}";
            _logger.LogWarning(ex, "PTY pane start failed (cwd={Cwd})", WorkingDirectory);
        }
    }

    /// <summary>Recent output history (bounded tail) — per-pane, never shared.</summary>
    public string History => OutputText;

    /// <summary>Recallable command history of this pane (empty in MVP — tty echo is the source of truth).</summary>
    public IReadOnlyList<string> CommandHistory => _commandHistory;

    /// <summary>Submit the input line to the shell (Enter).</summary>
    public void Submit()
    {
        if (_pty is null || IsClosed) return;
        string line = InputText;
        InputText = string.Empty;
        try
        {
            _pty.WriteLine(line);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PTY write failed (pid={Pid})", _pty.Pid);
        }
    }

    /// <summary>Recall the previous command into the input box (Up).</summary>
    public void HistoryPrevious()
    {
        if (_commandHistory.Count == 0) return;
        _historyIndex = Math.Max(0, _historyIndex - 1);
        InputText = _commandHistory[_historyIndex];
    }

    /// <summary>Recall the next command into the input box (Down).</summary>
    public void HistoryNext()
    {
        if (_commandHistory.Count == 0) return;
        _historyIndex = Math.Min(_commandHistory.Count, _historyIndex + 1);
        InputText = _historyIndex == _commandHistory.Count ? string.Empty : _commandHistory[_historyIndex];
    }

    private void OnOutputReceived(object? sender, PtyOutputEventArgs e)
    {
        // Incremental decode: UTF-8 sequences can split across PTY chunks.
        char[] chars = new char[e.Data.Length];
        int charCount = _decoder.GetChars(e.Data, 0, e.Data.Length, chars, 0);
        string text = Ansi.StripControlSequences(new string(chars, 0, charCount));
        if (text.Length == 0) return;

        _dispatcher.Post(() =>
        {
            _buffer.Append(text);
            if (_buffer.Length > MaxBufferChars)
            {
                _buffer.Remove(0, _buffer.Length - MaxBufferChars);
            }

            OutputText = _buffer.ToString();
        });
    }

    private void OnOutputClosed(object? sender, EventArgs e)
        => _dispatcher.Post(() =>
        {
            IsClosed = true;
            _buffer.Append("\n[process exited]\n");
            OutputText = _buffer.ToString();
        });

    public void Dispose()
    {
        if (_pty is null) return;
        _pty.OutputReceived -= OnOutputReceived;
        _pty.OutputClosed -= OnOutputClosed;
        // PtyProcess is IAsyncDisposable; sync dispose sites (window close, pane close)
        // start the teardown and observe faults — never an unobserved fire-and-forget.
        ILogger logger = _logger;
        _ = _pty.DisposeAsync().AsTask().ContinueWith(
            t => logger.LogWarning(t.Exception, "PTY dispose failed"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }
}

/// <summary>Minimal ANSI/VT control-sequence stripper for plain-text pane output.</summary>
public static class Ansi
{
    /// <summary>Removes CSI/OSC sequences and other C0 control characters except \n, \r, \t.</summary>
    public static string StripControlSequences(string input)
    {
        if (input.IndexOf('\x1b') < 0 && input.IndexOf('\x7') < 0)
        {
            return Normalize(input);
        }

        var sb = new StringBuilder(input.Length);
        int i = 0;
        while (i < input.Length)
        {
            char c = input[i];
            if (c == '\x1b' && i + 1 < input.Length)
            {
                char next = input[i + 1];
                if (next == '[') // CSI: params + final byte 0x40–0x7E
                {
                    i += 2;
                    while (i < input.Length && (input[i] < '\x40' || input[i] > '\x7e')) i++;
                    i++; // consume final byte
                    continue;
                }

                if (next == ']') // OSC: terminated by BEL or ST
                {
                    int end = input.IndexOf('\x7', i + 2);
                    if (end < 0)
                    {
                        int st = input.IndexOf("\x1b\\", i + 2, StringComparison.Ordinal);
                        end = st < 0 ? input.Length : st + 1;
                    }

                    i = end + 1;
                    continue;
                }

                i += 2; // two-byte escapes (ESC 7, ESC 8, …)
                continue;
            }

            if (c == '\x7')
            {
                i++; // BEL
                continue;
            }

            sb.Append(c);
            i++;
        }

        return Normalize(sb.ToString());

        static string Normalize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c is '\n' or '\r' or '\t' || (!char.IsControl(c) && c != '\x7f'))
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }
    }
}
