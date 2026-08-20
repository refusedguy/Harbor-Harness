using Harbor.Tools.Mcp;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tools.Builtin.Tests;

public class McpConfigTests
{
    [Test]
    public async Task McpJsonSerializerContext_Exists_AndIsPartial()
    {
        var ctx = McpJsonSerializerContext.Default;
        await Assert.That(ctx).IsNotNull();
    }

    [Test]
    public async Task RegisterFromConfig_LoadsServers_FromValidJson()
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, """
            {
                "test-server": "echo hello"
            }
            """);

            var loggerFactory = LoggerFactory.Create(b => { });
            var registry = new McpRegistry(loggerFactory.CreateLogger<McpRegistry>());
            var result = registry.RegisterFromConfig(tempFile);
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(registry.GetServerNames()).Contains("test-server");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Test]
    public async Task RegisterFromConfig_ReturnsSuccess_WhenFileMissing()
    {
            var loggerFactory = LoggerFactory.Create(b => { });
        var registry = new McpRegistry(loggerFactory.CreateLogger<McpRegistry>());
        var result = registry.RegisterFromConfig("/nonexistent/path/harbor.mcp.json");
        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task RegisterFromConfig_ParsesCommandWithArgs()
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, """
            {
                "my-server": {
                    "command": "node",
                    "args": ["-e", "console.log('ok')"]
                }
            }
            """);

            var loggerFactory = LoggerFactory.Create(b => { });
            var registry = new McpRegistry(loggerFactory.CreateLogger<McpRegistry>());
            var result = registry.RegisterFromConfig(tempFile);
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(registry.GetServerNames()).Contains("my-server");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
