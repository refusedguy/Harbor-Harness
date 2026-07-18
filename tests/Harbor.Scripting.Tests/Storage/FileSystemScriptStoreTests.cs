// Storage layer tests — FileSystemScriptStore (uses a temp directory).
using Harbor.Scripting.Storage;
namespace Harbor.Scripting.Tests.Storage;

public class FileSystemScriptStoreTests
{
    private static string NewTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "harbor-fs-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Test]
    public async Task ListAsync_EmptyDir_ReturnsEmptyList()
    {
        var dir = NewTempDir();
        try
        {
            var store = new FileSystemScriptStore(dir);

            var result = await store.ListAsync();

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value).IsEmpty();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* swallow */ }
        }
    }

    [Test]
    public async Task WriteAsync_CreatesTsFile_OnFirstWrite()
    {
        var dir = NewTempDir();
        try
        {
            var store = new FileSystemScriptStore(dir);

            await store.WriteAsync("greet", "Harbor.log('hi');");
            var read = await store.ReadAsync("greet");

            await Assert.That(read.IsSuccess).IsTrue();
            await Assert.That(read.Value.Name).IsEqualTo("greet");
            await Assert.That(read.Value.Content).IsEqualTo("Harbor.log('hi');");
            await Assert.That(File.Exists(Path.Combine(dir, "greet.ts"))).IsTrue();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* swallow */ }
        }
    }

    [Test]
    public async Task ListAsync_PicksUpJsAndTsFiles()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.ts"), "x");
            File.WriteAllText(Path.Combine(dir, "b.js"), "y");
            File.WriteAllText(Path.Combine(dir, "c.txt"), "ignored");
            var store = new FileSystemScriptStore(dir, createRoot: false);

            var result = await store.ListAsync();

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Count).IsEqualTo(2);
            await Assert.That(result.Value[0].Name).IsEqualTo("a");
            await Assert.That(result.Value[1].Name).IsEqualTo("b");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* swallow */ }
        }
    }

    [Test]
    public async Task DeleteAsync_RemovesFile()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "temp.ts"), "x");
            var store = new FileSystemScriptStore(dir, createRoot: false);

            var result = await store.DeleteAsync("temp");

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(File.Exists(Path.Combine(dir, "temp.ts"))).IsFalse();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* swallow */ }
        }
    }

    [Test]
    public async Task ReadAsync_MissingScript_ReturnsFailure()
    {
        var dir = NewTempDir();
        try
        {
            var store = new FileSystemScriptStore(dir, createRoot: false);

            var result = await store.ReadAsync("nonexistent");

            await Assert.That(result.IsSuccess).IsFalse();
            await Assert.That(result.Error).Contains("not found");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* swallow */ }
        }
    }

    [Test]
    public async Task ListAsync_FirstRootWinsOnNameCollision()
    {
        var dir1 = NewTempDir();
        var dir2 = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir1, "tool.ts"), "from-dir1");
            File.WriteAllText(Path.Combine(dir2, "tool.js"), "from-dir2");
            var store = new FileSystemScriptStore((IEnumerable<string>)new[] { dir1, dir2 }, createRoots: false);

            var list = await store.ListAsync();
            var read = await store.ReadAsync("tool");

            await Assert.That(list.IsSuccess).IsTrue();
            await Assert.That(list.Value.Count).IsEqualTo(1);
            await Assert.That(read.Value.Content).IsEqualTo("from-dir1");
        }
        finally
        {
            try { Directory.Delete(dir1, true); } catch { /* swallow */ }
            try { Directory.Delete(dir2, true); } catch { /* swallow */ }
        }
    }
}
