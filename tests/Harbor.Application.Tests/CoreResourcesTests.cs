using Harbor.Application.Permissions;
using System.Reflection;
namespace Harbor.Application.Tests;

public class CoreResourcesTests
{
    private static readonly Type CoreResourcesType = typeof(PermissionService).Assembly.GetType("Harbor.Application.Resources.CoreResources")!;
    private static readonly MethodInfo GetLogMethod = CoreResourcesType.GetMethod("GetLog", BindingFlags.Public | BindingFlags.Static)!;
    private static readonly MethodInfo GetErrorMethod = CoreResourcesType.GetMethod("GetError", BindingFlags.Public | BindingFlags.Static)!;

    private static string GetLog(string name) => (string)GetLogMethod.Invoke(null, new object[] { name })!;
    private static string GetError(string name) => (string)GetErrorMethod.Invoke(null, new object[] { name })!;

    [Test]
    public async Task GetLog_ReturnsString_ForKnownKey()
    {
        string result = GetLog("AgentLoopStarting");
        await Assert.That(result).IsNotNull();
        await Assert.That(result.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task GetLog_ReturnsKey_ForUnknownKey() => await Assert.That(GetLog("NonExistentKey123")).IsEqualTo("NonExistentKey123");

    [Test]
    public async Task GetError_ReturnsString_ForKnownKey()
    {
        string result = GetError("SessionNotFound");
        await Assert.That(result).IsNotNull();
        await Assert.That(result).Contains("Session");
    }

    [Test]
    public async Task GetError_ReturnsKey_ForUnknownKey() => await Assert.That(GetError("NonExistentError456")).IsEqualTo("NonExistentError456");

    [Test]
    public async Task KnownKeys_AllReturnNonEmpty()
    {
        string[] logKeys = new[] { "AgentLoopStarting", "OpenSessionFailed", "DeleteSessionFailed", "RenameSessionFailed", "AgentFailed" };
        string[] errorKeys = new[] { "SessionNotFound", "OperationCancelled", "ToolNotRegistered", "PermissionDenied", "InvalidHarborMode" };

        foreach (string key in logKeys)
        {
            string value = GetLog(key);
            await Assert.That(value).IsNotNull();
            await Assert.That(value.Length).IsGreaterThan(0);
        }

        foreach (string key in errorKeys)
        {
            string value = GetError(key);
            await Assert.That(value).IsNotNull();
            await Assert.That(value.Length).IsGreaterThan(0);
        }
    }
}
