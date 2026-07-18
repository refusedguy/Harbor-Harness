// Storage layer tests — InMemoryScriptStore.
using Harbor.Scripting.Storage;
namespace Harbor.Scripting.Tests.Storage;

public class InMemoryScriptStoreTests
{
    [Test]
    public async Task ListAsync_EmptyStore_ReturnsEmptyList()
    {
        var store = new InMemoryScriptStore();

        var result = await store.ListAsync();

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEmpty();
    }

    [Test]
    public async Task WriteAsync_ThenReadAsync_ReturnsWrittenContent()
    {
        var store = new InMemoryScriptStore();

        var writeResult = await store.WriteAsync("greet", "Harbor.log('hi');");
        var readResult = await store.ReadAsync("greet");

        await Assert.That(writeResult.IsSuccess).IsTrue();
        await Assert.That(readResult.IsSuccess).IsTrue();
        await Assert.That(readResult.Value.Name).IsEqualTo("greet");
        await Assert.That(readResult.Value.Content).IsEqualTo("Harbor.log('hi');");
        await Assert.That(readResult.Value.Hash).IsNotNull();
        await Assert.That(readResult.Value.Hash.Length).IsEqualTo(64);
    }

    [Test]
    public async Task DeleteAsync_ExistingScript_RemovesIt()
    {
        var store = new InMemoryScriptStore();
        await store.WriteAsync("temp", "console.log('x')");

        var deleteResult = await store.DeleteAsync("temp");
        var readResult = await store.ReadAsync("temp");

        await Assert.That(deleteResult.IsSuccess).IsTrue();
        await Assert.That(readResult.IsSuccess).IsFalse();
    }

    [Test]
    public async Task DeleteAsync_MissingScript_ReturnsFailure()
    {
        var store = new InMemoryScriptStore();

        var result = await store.DeleteAsync("nonexistent");

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Error).Contains("not found");
    }

    [Test]
    public async Task ListAsync_ReturnsEntriesSortedByName()
    {
        var store = new InMemoryScriptStore();
        await store.WriteAsync("zeta", "1");
        await store.WriteAsync("alpha", "2");
        await store.WriteAsync("mid", "3");

        var result = await store.ListAsync();

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Count).IsEqualTo(3);
        await Assert.That(result.Value[0].Name).IsEqualTo("alpha");
        await Assert.That(result.Value[1].Name).IsEqualTo("mid");
        await Assert.That(result.Value[2].Name).IsEqualTo("zeta");
    }

    [Test]
    public async Task WriteAsync_EmptyName_ReturnsFailure()
    {
        var store = new InMemoryScriptStore();

        var result = await store.WriteAsync("   ", "content");

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Error).Contains("empty");
    }

    [Test]
    public async Task SeedConstructor_PopulatesEntries()
    {
        var store = new InMemoryScriptStore(new Dictionary<string, string>
        {
            ["a"] = "content-a",
            ["b"] = "content-b"
        });

        var result = await store.ListAsync();

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Count).IsEqualTo(2);
    }
}
