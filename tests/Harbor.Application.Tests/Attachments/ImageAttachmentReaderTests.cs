using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Application.Attachments;

namespace Harbor.Application.Tests.Attachments;

/// <summary>
///     Issue #386 — attach-time validation. Every rejection has to arrive as a
///     <see cref="Result" /> failure with a readable reason, because the
///     alternative (a provider 400 mid-stream) costs the user a whole turn.
/// </summary>
public class ImageAttachmentReaderTests
{
    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"harbor-attach-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>1×1 PNG — real signature + IHDR, so both the magic-byte and
    /// dimension probes have something to read.</summary>
    private static byte[] OnePixelPng() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89
    ];

    [Test]
    public async Task ReadAsync_ValidPng_SucceedsWithProbedMimeAndDimensions()
    {
        string dir = CreateTempDir();
        try
        {
            string path = Path.Combine(dir, "shot.png");
            await File.WriteAllBytesAsync(path, OnePixelPng());

            var reader = new ImageAttachmentReader();
            var result = await reader.ReadAsync(path, "kilocode", "kilo-auto/free");

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.MimeType).IsEqualTo("image/png");
            await Assert.That(result.Value.Width).IsEqualTo(1);
            await Assert.That(result.Value.Height).IsEqualTo(1);
            await Assert.That(result.Value.HasDimensions).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task ReadAsync_MissingFile_FailsWithPathInTheMessage()
    {
        var reader = new ImageAttachmentReader();
        var result = await reader.ReadAsync("/nope/does-not-exist.png", "kilocode", "kilo-auto/free");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("not found");
    }

    [Test]
    public async Task ReadAsync_ProbedMimeWinsOverALyingExtension()
    {
        // A JPEG named .png: the wire MIME must be image/jpeg, or the provider
        // rejects the request.
        string dir = CreateTempDir();
        try
        {
            // SOI + APP0 + SOF0 carrying 400×600.
            var jpeg = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
            jpeg.AddRange(new byte[14]);
            jpeg.AddRange([0xFF, 0xC0, 0x00, 0x11, 0x08, 0x02, 0x58, 0x01, 0x90]);
            jpeg.AddRange(new byte[10]);

            string path = Path.Combine(dir, "actually-a-jpeg.png");
            await File.WriteAllBytesAsync(path, [.. jpeg]);

            var reader = new ImageAttachmentReader();
            var result = await reader.ReadAsync(path, "kilocode", "kilo-auto/free");

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.MimeType).IsEqualTo("image/jpeg");
            await Assert.That(result.Value.Width).IsEqualTo(400);
            await Assert.That(result.Value.Height).IsEqualTo(600);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task ReadAsync_EmptyPath_FailsWithUsage()
    {
        var reader = new ImageAttachmentReader();
        var result = await reader.ReadAsync("   ", "kilocode", "kilo-auto/free");

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("/attach");
    }

    [Test]
    public async Task ReadAsync_EmptyFile_Fails()
    {
        string dir = CreateTempDir();
        try
        {
            string path = Path.Combine(dir, "empty.png");
            await File.WriteAllBytesAsync(path, []);

            var reader = new ImageAttachmentReader();
            var result = await reader.ReadAsync(path, "kilocode", "kilo-auto/free");

            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(result.Error).Contains("empty");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task ReadAsync_UnsupportedFormat_FailsAndNamesTheAcceptedOnes()
    {
        string dir = CreateTempDir();
        try
        {
            // A .png extension on text bytes: the probe must not trust it.
            string path = Path.Combine(dir, "fake.png");
            await File.WriteAllTextAsync(path, "not an image at all");

            var reader = new ImageAttachmentReader();
            var result = await reader.ReadAsync(path, "kilocode", "kilo-auto/free");

            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(result.Error).Contains("Unsupported image format");
            await Assert.That(result.Error).Contains("PNG");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task ReadAsync_OversizedFile_FailsWithTheLimitNamed()
    {
        string dir = CreateTempDir();
        try
        {
            // One byte over the cap, prefixed with a valid PNG signature so the
            // size check is what rejects it (not the format check).
            var oversized = new byte[ImageAttachmentReader.MaxBytes + 1];
            OnePixelPng().CopyTo(oversized.AsSpan());
            string path = Path.Combine(dir, "huge.png");
            await File.WriteAllBytesAsync(path, oversized);

            var reader = new ImageAttachmentReader();
            var result = await reader.ReadAsync(path, "kilocode", "kilo-auto/free");

            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(result.Error).Contains("too large");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task ReadAsync_ModelWithoutVision_FailsAtAttachTime()
    {
        string dir = CreateTempDir();
        try
        {
            string path = Path.Combine(dir, "shot.png");
            await File.WriteAllBytesAsync(path, OnePixelPng());

            var registry = new StubProviderRegistry(
                [new ModelInfo("text-only", "kilocode", "Text Only", 1000, 100, false, false, true, Pricing.Unknown, "kilocode")]);
            var reader = new ImageAttachmentReader(registry);

            var result = await reader.ReadAsync(path, "kilocode", "text-only");

            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(result.Error).Contains("does not accept images");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task ReadAsync_VisionModel_Succeeds()
    {
        string dir = CreateTempDir();
        try
        {
            string path = Path.Combine(dir, "shot.png");
            await File.WriteAllBytesAsync(path, OnePixelPng());

            var registry = new StubProviderRegistry(
                [new ModelInfo("sees", "kilocode", "Sees", 1000, 100, false, true, true, Pricing.Unknown, "kilocode")]);
            var reader = new ImageAttachmentReader(registry);

            var result = await reader.ReadAsync(path, "kilocode", "sees");

            await Assert.That(result.IsSuccess).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task ReadAsync_ModelAbsentFromCatalog_AllowsTheAttach()
    {
        // A catalog that doesn't list the model proves nothing — refusing here
        // would block every provider whose JSON omits the vision flag.
        string dir = CreateTempDir();
        try
        {
            string path = Path.Combine(dir, "shot.png");
            await File.WriteAllBytesAsync(path, OnePixelPng());

            var reader = new ImageAttachmentReader(new StubProviderRegistry([]));
            var result = await reader.ReadAsync(path, "kilocode", "unlisted-model");

            await Assert.That(result.IsSuccess).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task ReadAsync_CatalogUnavailable_AllowsTheAttach()
    {
        string dir = CreateTempDir();
        try
        {
            string path = Path.Combine(dir, "shot.png");
            await File.WriteAllBytesAsync(path, OnePixelPng());

            var reader = new ImageAttachmentReader(new StubProviderRegistry([], failCatalog: true));
            var result = await reader.ReadAsync(path, "kilocode", "any-model");

            await Assert.That(result.IsSuccess).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private sealed class StubProviderRegistry(IReadOnlyList<ModelInfo> models, bool failCatalog = false) : IProviderRegistry
    {
        public IReadOnlyList<ProviderId> GetRegisteredProviderIds() => [ProviderId.Create("kilocode")];
        public Result<ILlmClient> GetClient(ProviderId providerId) => Result.Failure<ILlmClient>("unused");

        public Task<Result<IReadOnlyList<ModelInfo>>> GetAllModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(models));

        public Task<Result<IReadOnlyList<ModelInfo>>> GetModelsCachedAsync(
            ProviderId providerId, CancellationToken cancellationToken = default) =>
            failCatalog
                ? Task.FromResult(Result.Failure<IReadOnlyList<ModelInfo>>("catalog offline"))
                : Task.FromResult(Result.Success(models));

        public void Register(ProviderId providerId, Func<ILlmClient> factory) { }
        public Result Unregister(ProviderId providerId) => Result.Success();
    }
}
