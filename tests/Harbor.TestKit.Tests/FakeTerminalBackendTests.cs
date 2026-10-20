using System.Text;

namespace Harbor.TestKit.Tests;

/// <summary>
///     The terminal-backend doubles. The property worth pinning is the one
///     the async-only backend exists for: a backend that cannot ship a frame
///     synchronously must be TYPED differently, because the product puts the
///     backend's type name in the exception it throws.
/// </summary>
public class FakeTerminalBackendTests
{
    [Test]
    public async Task The_Recording_Backend_Captures_Every_Frame()
    {
        var backend = new RecordingBackend();

        await backend.WriteAsync(Encoding.UTF8.GetBytes("hello "));
        backend.Write(Encoding.UTF8.GetBytes("world"));

        await Assert.That(backend.WriteCount).IsEqualTo(2);
        await Assert.That(backend.TotalBytes).IsEqualTo(11);
        await Assert.That(backend.Text).IsEqualTo("hello world");
    }

    [Test]
    public async Task The_Recording_Backend_Escapes_Control_Characters()
    {
        var backend = new RecordingBackend();

        backend.Write(Encoding.UTF8.GetBytes("a\rb\nc"));

        await Assert.That(backend.Escaped).IsEqualTo("a\\rb\\nc")
            .Because("a raw CR or LF in a failure message is unreadable, and the whole point of the escaped form is that a comparison never sees one");
    }

    [Test]
    public async Task The_Recording_Backend_Forgets_On_Reset()
    {
        var backend = new RecordingBackend();
        backend.Write(Encoding.UTF8.GetBytes("x"));

        backend.Reset();

        await Assert.That(backend.WriteCount).IsEqualTo(0);
        await Assert.That(backend.Text).IsEmpty();
    }

    [Test]
    public async Task The_Counting_Backend_Counts_Without_Copying()
    {
        var backend = new CountingBackend();

        await backend.WriteAsync(Encoding.UTF8.GetBytes("12345"));
        backend.Write(Encoding.UTF8.GetBytes("67"));

        await Assert.That(backend.Writes).IsEqualTo(2);
        await Assert.That(backend.Bytes).IsEqualTo(7)
            .Because("the counting backend exists for allocation probes: a backend that copied every frame would allocate the very thing the probe measures");
    }

    [Test]
    public async Task The_Counting_Backend_Resets()
    {
        var backend = new CountingBackend();
        await backend.WriteAsync(Encoding.UTF8.GetBytes("12345"));

        backend.Reset();

        await Assert.That(backend.Writes).IsEqualTo(0);
        await Assert.That(backend.Bytes).IsEqualTo(0);
    }

    [Test]
    public async Task The_Null_Backend_Absorbs_Every_Frame()
    {
        var backend = new NullTerminalBackend();

        await backend.WriteAsync(Encoding.UTF8.GetBytes("x"));
        backend.Write(Encoding.UTF8.GetBytes("y"));

        // The assertion here is that absorbing a frame does not throw — the
        // null backend exists for the probes that measure the encoder, not the
        // output device.
        await Assert.That(backend).IsNotNull();
    }

    [Test]
    public async Task The_Async_Only_Backend_Counts_And_Keeps_The_Frames()
    {
        var backend = new AsyncOnlyBackend();

        await backend.WriteAsync(Encoding.UTF8.GetBytes("ab"));

        await Assert.That(backend.Writes).IsEqualTo(1);
        await Assert.That(backend.Frames.Count).IsEqualTo(1);
        await Assert.That(backend.Text).IsEqualTo("ab");
    }

    [Test]
    public async Task The_Async_Only_Backend_Is_Not_A_Sync_One()
    {
        var backend = new AsyncOnlyBackend();

        await Assert.That(backend is Harbor.Tui.CellForge.Rendering.ISyncTerminalBackend).IsFalse()
            .Because("the async-only backend is a statement about the contract: the product throws when a synchronous flush is asked of it, and the test asserts on the backend's type name in that message");
    }
}
