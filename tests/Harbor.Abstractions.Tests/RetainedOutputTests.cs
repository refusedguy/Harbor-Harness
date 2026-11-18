// RetainedOutputTests.cs — issue #2672 (F06).
//
// The retained-output value is the small contract that stops an oversized tool
// output from being either all inline or gone. These tests pin the two rules a
// consumer relies on: retention is bounded to 1 MiB, and a read hands back a
// chunk that is never cut in the middle of a character.

using Harbor.Abstractions.Models;

namespace Harbor.Abstractions.Tests;

public class RetainedOutputTests
{
    [Test]
    public async Task Create_KeepsASmallOutputWhole()
    {
        RetainedOutput output = RetainedOutput.Create("tc-1", "hello", 5);

        await Assert.That(output.ToolCallId).IsEqualTo("tc-1");
        await Assert.That(output.Text).IsEqualTo("hello");
        await Assert.That(output.TotalBytes).IsEqualTo(5);
        await Assert.That(output.RetainedBytes).IsEqualTo(5);
        await Assert.That(output.IsTruncated).IsFalse();
    }

    [Test]
    public async Task Create_ClampsToOneMiB_AndReportsTruncation()
    {
        string full = new string('x', RetainedOutput.MaxRetainedBytes + 100);

        RetainedOutput output = RetainedOutput.Create("tc-2", full, full.Length);

        await Assert.That(output.RetainedBytes).IsEqualTo(RetainedOutput.MaxRetainedBytes);
        await Assert.That(output.Text.Length).IsEqualTo(RetainedOutput.MaxRetainedBytes);
        await Assert.That(output.TotalBytes).IsEqualTo(full.Length);
        await Assert.That(output.IsTruncated).IsTrue();
    }

    [Test]
    public async Task ReadChunk_ReturnsBoundedSlices()
    {
        var output = new RetainedOutput("tc-3", "0123456789", 10);

        await Assert.That(output.ReadChunk(0, 4)).IsEqualTo("0123");
        await Assert.That(output.ReadChunk(4, 4)).IsEqualTo("4567");
        await Assert.That(output.ReadChunk(8, 4)).IsEqualTo("89");
        await Assert.That(output.ReadChunk(10, 4)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task ReadChunk_DefaultsToOneEightKiB()
    {
        string text = new string('a', RetainedOutput.ReadChunkBytes + 16);
        var output = new RetainedOutput("tc-4", text, text.Length);

        string chunk = output.ReadChunk(0);

        await Assert.That(chunk.Length).IsEqualTo(RetainedOutput.ReadChunkBytes);
        await Assert.That(output.ReadChunk(RetainedOutput.ReadChunkBytes)).IsEqualTo("aaaaaaaaaaaaaaaa");
    }

    [Test]
    public async Task ReadChunk_NeverEndsInsideACharacter()
    {
        // Four ASCII bytes short of the chunk boundary, then a four-byte rune.
        string text = new string('a', RetainedOutput.ReadChunkBytes - 1) + "😀" + "tail";
        var output = new RetainedOutput("tc-5", text, text.Length);

        string first = output.ReadChunk(0, RetainedOutput.ReadChunkBytes);

        await Assert.That(first.Length).IsEqualTo(RetainedOutput.ReadChunkBytes - 1);
        await Assert.That(first.Contains('\uFFFD')).IsFalse();
        await Assert.That(output.ReadChunk(first.Length, 4)).IsEqualTo("😀");
        await Assert.That(output.ReadChunk(first.Length + 4, 4)).IsEqualTo("tail");
    }

    [Test]
    public async Task ReadChunk_RejectsInvalidRanges()
    {
        var output = new RetainedOutput("tc-6", "abc", 3);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Task.Run(() => output.ReadChunk(-1, 2)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Task.Run(() => output.ReadChunk(0, 0)));
    }
}
