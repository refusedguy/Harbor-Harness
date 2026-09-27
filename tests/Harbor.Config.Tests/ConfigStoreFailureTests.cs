using Harbor.Application.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Config.Tests;

/// <summary>
///     Issue #202 (B4) regression tests: <see cref="JsonConfigStore.UpdateAsync" />
///     and <see cref="JsonConfigStore.GetApiKeyAsync" /> must surface a corrupt
///     config file as a <c>Result</c> failure — never throw, and never invoke
///     the updater on the failure path (the guarded <c>loadResult.Value</c> sites).
/// </summary>
public class ConfigStoreFailureTests
{
    private static string NewTempConfigPath() =>
        Path.Combine(Path.GetTempPath(), $"harbor-config-fail-{Guid.NewGuid():N}", "config.json");

    private static string WriteTempConfig(string content)
    {
        string path = NewTempConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static void DeleteTempConfig(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        string? dir = Path.GetDirectoryName(path);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    [Test]
    public async Task LoadAsync_CorruptFile_ReturnsFailureInsteadOfThrowing()
    {
        string path = WriteTempConfig("{ this is not valid json !!!");
        try
        {
            var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);

            var result = await store.LoadAsync();

            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(result.Error).Contains("corrupt");
        }
        finally
        {
            DeleteTempConfig(path);
        }
    }

    [Test]
    public async Task UpdateAsync_CorruptFile_ReturnsFailureWithoutInvokingUpdater()
    {
        string path = WriteTempConfig("{ this is not valid json !!!");
        try
        {
            var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);
            bool updaterCalled = false;

            var result = await store.UpdateAsync(c =>
            {
                updaterCalled = true;
                return c;
            });

            await Assert.That(result.IsFailure).IsTrue();
            await Assert.That(updaterCalled).IsFalse();
        }
        finally
        {
            DeleteTempConfig(path);
        }
    }

    [Test]
    public async Task GetApiKeyAsync_CorruptFile_ReturnsFailureInsteadOfThrowing()
    {
        string path = WriteTempConfig("{ this is not valid json !!!");
        try
        {
            var store = new JsonConfigStore(path, NullLogger<JsonConfigStore>.Instance);

            var result = await store.GetApiKeyAsync("anthropic");

            await Assert.That(result.IsFailure).IsTrue();
        }
        finally
        {
            DeleteTempConfig(path);
        }
    }
}
