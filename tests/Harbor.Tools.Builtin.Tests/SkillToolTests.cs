using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Harbor.Tools.Builtin.Tests;

public class SkillToolTests
{
    private static ToolContext CreateContext(IServiceProvider? services = null)
        => new(
            "test-session",
            "test-message",
            "test-call",
            "code",
            CancellationToken.None,
            Array.Empty<Harbor.Abstractions.Models.AgentMessage>(),
            (_, _) => Task.CompletedTask,
            (_, _) => Task.FromResult(new Harbor.Abstractions.Permissions.PermissionResponse(Harbor.Abstractions.Permissions.PermissionAction.Allow, false)),
            services ?? Mock.Of<IServiceProvider>());

    [Test]
    public async Task Name_IsSkill()
    {
        var tool = new SkillTool(NullLogger<SkillTool>.Instance);
        await Assert.That(tool.Name.Value).IsEqualTo("skill");
    }

    [Test]
    public async Task ExecutionMode_IsParallel()
    {
        var tool = new SkillTool(NullLogger<SkillTool>.Instance);
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Parallel);
    }

    [Test]
    public async Task ValidateArguments_MissingName_ReturnsFailure()
    {
        var tool = new SkillTool(NullLogger<SkillTool>.Instance);
        var args = JsonDocument.Parse("{}").RootElement;
        var result = tool.ValidateArguments(args);
        await Assert.That(result.IsFailure).IsTrue();
    }

    [Test]
    public async Task ExecuteAsync_NoProvider_ReturnsError()
    {
        var tool = new SkillTool(NullLogger<SkillTool>.Instance);
        var args = JsonDocument.Parse("""{"name":"foo"}""").RootElement;
        var result = await tool.ExecuteAsync(args, CreateContext());
        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("not available");
    }

    [Test]
    public async Task ExecuteAsync_SkillNotFound_ReturnsError()
    {
        var provider = new Mock<ISkillProvider>();
        provider.Setup(p => p.ReadSkill(It.IsAny<string>(), It.IsAny<string>())).Returns((string?)null);
        var sp = new ServiceCollection().AddSingleton(provider.Object).BuildServiceProvider();

        var tool = new SkillTool(NullLogger<SkillTool>.Instance);
        var args = JsonDocument.Parse("""{"name":"missing"}""").RootElement;
        var result = await tool.ExecuteAsync(args, CreateContext(sp));
        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("not found");
    }

    [Test]
    public async Task ExecuteAsync_SkillFound_ReturnsContent()
    {
        var provider = new Mock<ISkillProvider>();
        provider.Setup(p => p.ReadSkill(It.IsAny<string>(), "myskill")).Returns("# My Skill\n\nContent here.");
        var sp = new ServiceCollection().AddSingleton(provider.Object).BuildServiceProvider();

        var tool = new SkillTool(NullLogger<SkillTool>.Instance);
        var args = JsonDocument.Parse("""{"name":"myskill"}""").RootElement;
        var result = await tool.ExecuteAsync(args, CreateContext(sp));
        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).Contains("# My Skill");
        await Assert.That(result.Output).Contains("Content here.");
    }
}
