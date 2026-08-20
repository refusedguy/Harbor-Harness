using System.Reflection;
using Harbor.Core.Permissions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

public class CoreResourcesTests
{
    private static readonly Type CoreResourcesType = typeof(PermissionService).Assembly.GetType("Harbor.Core.Resources.CoreResources")!;
    private static readonly MethodInfo GetLogMethod = CoreResourcesType.GetMethod("GetLog", BindingFlags.Public | BindingFlags.Static)!;
    private static readonly MethodInfo GetErrorMethod = CoreResourcesType.GetMethod("GetError", BindingFlags.Public | BindingFlags.Static)!;

    private static string GetLog(string name) => (string)GetLogMethod.Invoke(null, new object[] { name })!;
    private static string GetError(string name) => (string)GetErrorMethod.Invoke(null, new object[] { name })!;

    [Test]
    public async Task GetLog_ReturnsString_ForKnownKey()
    {
        var result = GetLog("AgentLoopStarting");
        await Assert.That(result).IsNotNull();
        await Assert.That(result.Length).IsGreaterThan(0);
    }

    [Test]
    public async Task GetLog_ReturnsKey_ForUnknownKey()
    {
        await Assert.That(GetLog("NonExistentKey123")).IsEqualTo("NonExistentKey123");
    }

    [Test]
    public async Task GetError_ReturnsString_ForKnownKey()
    {
        var result = GetError("SessionNotFound");
        await Assert.That(result).IsNotNull();
        await Assert.That(result).Contains("Session");
    }

    [Test]
    public async Task GetError_ReturnsKey_ForUnknownKey()
    {
        await Assert.That(GetError("NonExistentError456")).IsEqualTo("NonExistentError456");
    }

    [Test]
    public async Task KnownKeys_AllReturnNonEmpty()
    {
        var logKeys = new[] { "AgentLoopStarting", "OpenSessionFailed", "DeleteSessionFailed", "RenameSessionFailed", "AgentFailed" };
        var errorKeys = new[] { "SessionNotFound", "OperationCancelled", "ToolNotRegistered", "PermissionDenied", "InvalidHarborMode" };

        foreach (var key in logKeys)
        {
            var value = GetLog(key);
            await Assert.That(value).IsNotNull();
            await Assert.That(value.Length).IsGreaterThan(0);
        }

        foreach (var key in errorKeys)
        {
            var value = GetError(key);
            await Assert.That(value).IsNotNull();
            await Assert.That(value.Length).IsGreaterThan(0);
        }
    }
}
