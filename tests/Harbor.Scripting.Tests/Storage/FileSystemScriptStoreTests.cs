// Storage layer tests — FileSystemScriptStore (uses a temp directory).
namespace Harbor.Scripting.Tests.Storage;
public class FileSystemScriptStoreTests
{
    private static string NewTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "harbor-fs-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Test]
    public async Task ListAsync_EmptyDir_ReturnsEmptyList()
    {
        string dir = NewTempDir();
        try
        {
            var store = new FileSystemScriptStore(dir);

            var result = await store.ListAsync();

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value).IsEmpty();
        }
        finally
        {
            try { Directory.Delete(dir, true); }
            catch
            { /* swallow */
            }
        }
    }

    [Test]
    public async Task WriteAsync_CreatesTsFile_OnFirstWrite()
    {
        string dir = NewTempDir();
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
            try { Directory.Delete(dir, true); }
            catch
            { /* swallow */
            }
        }
    }

    [Test]
    public async Task ListAsync_PicksUpJsAndTsFiles()
    {
        string dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.ts"), "x");
            File.WriteAllText(Path.Combine(dir, "b.js"), "y");
            File.WriteAllText(Path.Combine(dir, "c.txt"), "ignored");
            var store = new FileSystemScriptStore(dir, false);

            var result = await store.ListAsync();

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Count).IsEqualTo(2);
            await Assert.That(result.Value[0].Name).IsEqualTo("a");
            await Assert.That(result.Value[1].Name).IsEqualTo("b");
        }
        finally
        {
            try { Directory.Delete(dir, true); }
            catch
            { /* swallow */
            }
        }
    }

    [Test]
    public async Task DeleteAsync_RemovesFile()
    {
        string dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "temp.ts"), "x");
            var store = new FileSystemScriptStore(dir, false);

            var result = await store.DeleteAsync("temp");

            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(File.Exists(Path.Combine(dir, "temp.ts"))).IsFalse();
        }
        finally
        {
            try { Directory.Delete(dir, true); }
            catch
            { /* swallow */
            }
        }
    }

    [Test]
    public async Task ReadAsync_MissingScript_ReturnsFailure()
    {
        string dir = NewTempDir();
        try
        {
            var store = new FileSystemScriptStore(dir, false);

            var result = await store.ReadAsync("nonexistent");

            await Assert.That(result.IsSuccess).IsFalse();
            await Assert.That(result.Error).Contains("not found");
        }
        finally
        {
            try { Directory.Delete(dir, true); }
            catch
            { /* swallow */
            }
        }
    }

    [Test]
    public async Task ListAsync_FirstRootWinsOnNameCollision()
    {
        string dir1 = NewTempDir();
        string dir2 = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir1, "tool.ts"), "from-dir1");
            File.WriteAllText(Path.Combine(dir2, "tool.js"), "from-dir2");
            var store = new FileSystemScriptStore(new[] { dir1, dir2 }, false);

            var list = await store.ListAsync();
            var read = await store.ReadAsync("tool");

            await Assert.That(list.IsSuccess).IsTrue();
            await Assert.That(list.Value.Count).IsEqualTo(1);
            await Assert.That(read.Value.Content).IsEqualTo("from-dir1");
        }
        finally
        {
            try { Directory.Delete(dir1, true); }
            catch
            { /* swallow */
            }
            try { Directory.Delete(dir2, true); }
            catch
            { /* swallow */
            }
        }
    }
}
