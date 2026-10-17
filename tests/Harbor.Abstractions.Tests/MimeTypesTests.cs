using Harbor.Abstractions.Tools;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     Media-family predicate helpers (Harbor.Abstractions.Tools.MimeTypes,
///     #1578) — the single source of truth that replaced five hand copies of
///     <c>StartsWith("image/", ...)</c> across ReadTool, WebFetchTool and the
///     three CellForge Chat sites.
/// </summary>
public class MimeTypesTests
{
    [Test]
    public async Task IsImage_MatchesImageFamilyOnly()
    {
        await Assert.That(MimeTypes.IsImage("image/png")).IsTrue();
        await Assert.That(MimeTypes.IsImage("IMAGE/PNg")).IsTrue();
        await Assert.That(MimeTypes.IsImage("image/svg+xml")).IsTrue();
        await Assert.That(MimeTypes.IsImage("audio/mpeg")).IsFalse();
        await Assert.That(MimeTypes.IsImage("application/pdf")).IsFalse();
        await Assert.That(MimeTypes.IsImage("text/plain")).IsFalse();
    }

    [Test]
    public async Task IsAudio_MatchesAudioFamilyOnly()
    {
        await Assert.That(MimeTypes.IsAudio("audio/mpeg")).IsTrue();
        await Assert.That(MimeTypes.IsAudio("AUDIO/WAV")).IsTrue();
        await Assert.That(MimeTypes.IsAudio("audio/webm")).IsTrue();
        await Assert.That(MimeTypes.IsAudio("image/png")).IsFalse();
        await Assert.That(MimeTypes.IsAudio("video/mp4")).IsFalse();
        await Assert.That(MimeTypes.IsAudio("application/octet-stream")).IsFalse();
    }

    [Test]
    public async Task IsVideo_MatchesVideoFamilyOnly()
    {
        await Assert.That(MimeTypes.IsVideo("video/mp4")).IsTrue();
        await Assert.That(MimeTypes.IsVideo("VIDEO/WEBM")).IsTrue();
        await Assert.That(MimeTypes.IsVideo("video/quicktime")).IsTrue();
        await Assert.That(MimeTypes.IsVideo("image/png")).IsFalse();
        await Assert.That(MimeTypes.IsVideo("audio/mpeg")).IsFalse();
        await Assert.That(MimeTypes.IsVideo("application/pdf")).IsFalse();
    }

    [Test]
    public async Task NullOrEmpty_NeverMatchesAnyFamily()
    {
        await Assert.That(MimeTypes.IsImage(null)).IsFalse();
        await Assert.That(MimeTypes.IsAudio(null)).IsFalse();
        await Assert.That(MimeTypes.IsVideo(null)).IsFalse();
        await Assert.That(MimeTypes.IsImage("")).IsFalse();
        await Assert.That(MimeTypes.IsAudio("")).IsFalse();
        await Assert.That(MimeTypes.IsVideo("")).IsFalse();
    }

    [Test]
    public async Task FamilyOnlyAtKindPosition()
    {
        // "cellforge/image/png" belongs to no family: the prefix must be the
        // kind itself, not a suffix of another token.
        await Assert.That(MimeTypes.IsImage("ximage/png")).IsFalse();
        await Assert.That(MimeTypes.IsAudio("xaudio/mpeg")).IsFalse();
        await Assert.That(MimeTypes.IsVideo("xvideo/mp4")).IsFalse();
    }
}
