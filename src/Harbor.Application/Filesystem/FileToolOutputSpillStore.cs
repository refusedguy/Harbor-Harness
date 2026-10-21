using System.Text;
using Microsoft.Extensions.Logging;

namespace Harbor.Application.Filesystem;

/// <summary>
///     The file-backed spill area for oversized tool outputs (issue #1748).
/// </summary>
/// <remarks>
///     <para>
///         <b>Where the files live.</b> <c>$HARBOR_HOME/spill/&lt;session&gt;/</c> —
///         Harbor's private directory, one subdirectory per session, one file
///         per tool call. Nothing is ever written into the user's working tree,
///         and no path in here is ever derived from model input: the session id
///         and the tool call id are digested by
///         <see cref="ToolOutputSpillLayout" /> before they name anything.
///     </para>
///     <para>
///         <b>What a failure means.</b> A spill that cannot be written comes
///         back as a failed <see cref="Result{TValue}" />, and the caller keeps
///         the ORIGINAL output. That is the difference between an overfull
///         context and a silently cut one: the first is visible and recoverable,
///         the second is a lie told to the model.
///     </para>
///     <para>
///         <b>Cleanup.</b> The area is swept when a new session directory is
///         created — the one moment the store knows it is about to add to the
///         area — and every session directory whose last write is older than
///         the time-to-live is removed. The full output also lives in the
///         session log, so the file is a scratch pad for the run, not an
///         archive.
///     </para>
/// </remarks>
public sealed class FileToolOutputSpillStore : IToolOutputSpillStore
{
    /// <summary>
    ///     UTF-8 without a byte-order mark. A field rather than a property so
    ///     the encoder is allocated once — <see cref="Encoding" /> is immutable
    ///     and thread-safe.
    /// </summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    ///     The largest spilled output one read may load: 10 MiB, the same cap
    ///     the <c>read</c> tool uses for a text file.
    /// </summary>
    private const long MaxReadBytes = 10 * 1024 * 1024;

    private readonly string _root;
    private readonly TimeSpan _ttl;
    private readonly ILogger<FileToolOutputSpillStore> _logger;

    /// <summary>
    ///     Construct a spill store.
    /// </summary>
    /// <param name="logger">Logger for the failure trail.</param>
    /// <param name="root">The root of the spill area; defaults to <c>$HARBOR_HOME/spill</c>.</param>
    /// <param name="ttl">How long a session's spill directory is kept; defaults to 24 hours.</param>
    public FileToolOutputSpillStore(
        ILogger<FileToolOutputSpillStore> logger,
        string? root = null,
        TimeSpan? ttl = null)
    {
        _logger = logger;
        _root = root ?? ToolOutputSpillLayout.DefaultRoot;
        _ttl = ttl ?? TimeSpan.FromHours(ToolOutputSpillPolicy.DefaultTtlHours);
    }

    /// <inheritdoc />
    public string Root => _root;

    /// <inheritdoc />
    public Result<ToolOutputSpill> Spill(string sessionId, string toolCallId, string content)
    {
        try
        {
            string path = ToolOutputSpillLayout.PathFor(_root, sessionId, toolCallId);

            // Idempotent: a tool call spills the same bytes for the whole run,
            // and the context boundary asks for it on EVERY request. Rewriting
            // the file each time would turn a per-call write into a per-turn
            // write.
            if (File.Exists(path))
            {
                var existing = new FileInfo(path);
                return Result.Success(new ToolOutputSpill(path, existing.Length, content.Length));
            }

            string directory = ToolOutputSpillLayout.SessionDirectory(_root, sessionId);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                // The cleanup policy rides the creation of a session directory:
                // the one moment the store knows it is about to add to the area.
                // A failed sweep is logged by the sweep itself and must not
                // fail the write — the spill matters more than the housekeeping.
                _ = Sweep();
            }

            File.WriteAllText(path, content, Utf8NoBom);
            var written = new FileInfo(path);
            return Result.Success(new ToolOutputSpill(path, written.Length, content.Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            FileToolOutputSpillStoreLog.SpillFailed(_logger, ex, sessionId);
            return Result.Failure<ToolOutputSpill>($"Cannot spill the tool output: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<Result<string>> ReadAsync(
        string sessionId,
        string path,
        CancellationToken cancellationToken = default)
    {
        if (!ToolOutputSpillLayout.TryResolve(_root, sessionId, path, out string resolved))
        {
            return Result.Failure<string>($"'{path}' is not a spill file of this session.");
        }

        try
        {
            if (!File.Exists(resolved))
            {
                return Result.Failure<string>($"Spill file not found: {resolved}");
            }

            // The read is bounded: a spilled output is a tool's output, and the
            // tools bound theirs. The cap is the same one the `read` tool uses,
            // and a file past it is reported rather than loaded — the read is
            // there to give the model a piece back, not to hand it the whole
            // thing again.
            long length = new FileInfo(resolved).Length;
            if (length > MaxReadBytes)
            {
                return Result.Failure<string>(
                    $"The spill file is {length} bytes — too large to read in one go (max {MaxReadBytes}).");
            }

            return Result.Success(await File.ReadAllTextAsync(resolved, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileToolOutputSpillStoreLog.ReadFailed(_logger, ex, resolved);
            return Result.Failure<string>($"Cannot read the spill file '{resolved}': {ex.Message}");
        }
    }

    /// <inheritdoc />
    public Result<int> Sweep()
    {
        try
        {
            if (!Directory.Exists(_root))
            {
                return Result.Success(0);
            }

            DateTime cutoff = DateTime.UtcNow - _ttl;
            int removed = 0;
            foreach (string directory in Directory.EnumerateDirectories(_root))
            {
                var info = new DirectoryInfo(directory);

                // A link is never followed out of the spill area: the area holds
                // what this store wrote, and a link inside it is not that.
                if (info.LinkTarget is not null)
                {
                    continue;
                }

                if (info.LastWriteTimeUtc >= cutoff)
                {
                    continue;
                }

                Directory.Delete(directory, recursive: true);
                removed++;
            }

            return Result.Success(removed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileToolOutputSpillStoreLog.SweepFailed(_logger, ex, _root);
            return Result.Failure<int>($"Cannot sweep the spill area: {ex.Message}");
        }
    }
}

/// <summary>
///     SG1: BCL <c>[LoggerMessage]</c> delegates for the spill store. The
///     exception is the LAST parameter of each, so the generator renders it
///     with the message — the templates carry no <c>{Error}</c> placeholder,
///     because there is no such parameter to fill it with (SYSLIB1014).
/// </summary>
internal static partial class FileToolOutputSpillStoreLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Cannot spill the tool output of session {SessionId}")]
    public static partial void SpillFailed(ILogger logger, Exception ex, string sessionId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Cannot read the spill file {Path}")]
    public static partial void ReadFailed(ILogger logger, Exception ex, string path);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Cannot sweep the spill area {Root}")]
    public static partial void SweepFailed(ILogger logger, Exception ex, string root);
}
