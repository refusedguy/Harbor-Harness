using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Application.Attachments;

/// <summary>
///     Reads an image off disk into an <see cref="ImageAttachment" /> that can
///     ride along with a user turn (issue #386).
/// </summary>
/// <remarks>
///     <para>
///         The whole point of validating HERE rather than at request time is
///         that a provider 400 arrives minutes later, mid-stream, with a
///         message the model never sees. Every rejection is a
///         <see cref="Result{T}" /> failure carrying a user-readable reason —
///         missing file, oversized file, unsupported container, or a model
///         that has no vision at all.
///     </para>
///     <para>
///         Expected failures never throw: <see cref="Result" /> is the
///         contract, exceptions are reserved for genuinely unexpected faults
///         (see §ROP in <c>docs/CODE_PRINCIPLES_AUDIT.md</c>).
///     </para>
/// </remarks>
public sealed class ImageAttachmentReader
{
    /// <summary>
    ///     Hard cap on an attached image. Vision endpoints reject multi-megabyte
    ///     payloads (OpenAI: ~20 MB per request but effectively far less per
    ///     image), and every byte is base64-inflated 4/3 in the request body —
    ///     5 MB is the widest format (a 4K screenshot) that stays well inside
    ///     every provider's limit.
    /// </summary>
    public const long MaxBytes = 5 * 1024 * 1024;

    private readonly IProviderRegistry? _providers;
    private readonly ILogger _logger;

    /// <summary>
    ///     Create a reader.
    /// </summary>
    /// <param name="providers">
    ///     Model catalog used for the vision-capability check. Optional so hosts
    ///     without a provider registry (tests, minimal builds) still attach; the
    ///     capability probe then degrades to "unknown → allow".
    /// </param>
    /// <param name="logger">Diagnostics sink.</param>
    public ImageAttachmentReader(IProviderRegistry? providers = null, ILogger? logger = null)
    {
        _providers = providers;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    ///     Read and validate an image for the given model.
    /// </summary>
    /// <param name="path">Path to the image (resolved against the current directory when relative).</param>
    /// <param name="providerId">Provider the session is bound to.</param>
    /// <param name="modelId">Model the session is bound to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     The probed attachment, or a failure naming the exact reason
    ///     (not found / too large / unsupported format / model has no vision).
    /// </returns>
    public async Task<Result<ImageAttachment>> ReadAsync(
        string path, string providerId, string modelId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Result.Failure<ImageAttachment>("Usage: /attach <path-to-image>");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _logger.LogDebug(ex, "Rejected image path {Path}", path);
            return Result.Failure<ImageAttachment>($"Invalid image path: {path}");
        }

        var file = new FileInfo(fullPath);
        if (!file.Exists)
            return Result.Failure<ImageAttachment>($"Image not found: {path}");

        // Length is a TOCTOU-prone stat: a file removed between Exists and
        // Length throws, and one that grew after the stat must still be
        // rejected. So the stat is a cheap early-out, the re-check below is
        // the one that decides.
        long declaredLength;
        try
        {
            declaredLength = file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Failure<ImageAttachment>($"Cannot read image {path}: {ex.Message}");
        }

        if (declaredLength == 0)
            return Result.Failure<ImageAttachment>($"Image is empty: {path}");

        if (declaredLength > MaxBytes)
        {
            return Result.Failure<ImageAttachment>(
                $"Image is too large: {DescribeSize(declaredLength)} (max {DescribeSize(MaxBytes)})");
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to read image {Path}", path);
            return Result.Failure<ImageAttachment>($"Cannot read image {path}: {ex.Message}");
        }

        if (bytes.Length > MaxBytes)
        {
            return Result.Failure<ImageAttachment>(
                $"Image is too large: {DescribeSize(bytes.LongLength)} (max {DescribeSize(MaxBytes)})");
        }

        if (bytes.Length == 0)
            return Result.Failure<ImageAttachment>($"Image is empty: {path}");

        // Magic bytes, not the extension: a .png that is really a JPEG must
        // reach the provider with the MIME type it will actually accept.
        if (!ImageProbe.TryProbe(bytes, out string mimeType, out int width, out int height))
        {
            return Result.Failure<ImageAttachment>(
                $"Unsupported image format: {path} (expected {ImageProbe.SupportedFormats})");
        }

        var visionCheck = await CheckVisionAsync(providerId, modelId, ct).ConfigureAwait(false);
        if (visionCheck.IsFailure)
            return Result.Failure<ImageAttachment>(visionCheck.Error);

        _logger.LogDebug(
            "Attached {Path} as {MimeType} ({Width}×{Height}, {Bytes} bytes)",
            path, mimeType, width, height, bytes.Length);

        return Result.Success(new ImageAttachment(fullPath, mimeType, width, height, bytes));
    }

    /// <summary>
    ///     Ask the model catalog whether <paramref name="modelId" /> accepts
    ///     images. An unknown model (catalog unreachable, model not listed)
    ///     is NOT a rejection: refusing on missing metadata would block every
    ///     provider whose <c>providers/*.json</c> simply omits the flag.
    /// </summary>
    private async Task<Result> CheckVisionAsync(string providerId, string modelId, CancellationToken ct)
    {
        if (_providers is null || string.IsNullOrWhiteSpace(modelId))
            return Result.Success();

        var parsed = ProviderId.TryCreate(providerId);
        if (parsed.IsFailure)
            return Result.Success();

        var models = await _providers.GetModelsCachedAsync(parsed.Value, ct).ConfigureAwait(false);
        if (models.IsFailure)
        {
            _logger.LogDebug(
                "Vision check skipped for {Provider}/{Model}: catalog unavailable ({Error})",
                providerId, modelId, models.Error);
            return Result.Success();
        }

        ModelInfo? info = null;
        for (int i = 0; i < models.Value.Count; i++)
        {
            #pragma warning disable CFE0001
            // CFE0001: false positive — the IsFailure/IsSuccess guard is an early
            // return or continue, a control-flow shape the analyzer does not model.
            // The .Value is safe. Baseline: docs/ROP-API-INVENTORY.md §5.
            if (string.Equals(models.Value[i].Id, modelId, StringComparison.OrdinalIgnoreCase))
            #pragma warning restore CFE0001
            {
                #pragma warning disable CFE0001
                // CFE0001: false positive — the IsFailure/IsSuccess guard is an early
                // return or continue, a control-flow shape the analyzer does not model.
                // The .Value is safe. Baseline: docs/ROP-API-INVENTORY.md §5.
                info = models.Value[i];
                #pragma warning restore CFE0001
                break;
            }
        }

        if (info is null)
            return Result.Success();

        if (info.SupportsVision)
            return Result.Success();

        return Result.Failure($"Model '{modelId}' does not accept images (no vision support).");
    }

    private static string DescribeSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024 * 1024)} MB"
        : $"{Math.Max(1, bytes / 1024)} KB";
}
