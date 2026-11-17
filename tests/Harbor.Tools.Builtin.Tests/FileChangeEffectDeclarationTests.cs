using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     #1100: the producer side of the typed diff flag. The diff preview no
///     longer sniffs "Wrote " and "Edited " out of the output, so the two
///     tools that change files must declare the effect on their result.
/// </summary>
public class FileChangeEffectDeclarationTests
{
    [Test]
    public async Task WriteTool_Success_DeclaresFileChange()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"harbor-effect-{Guid.NewGuid():N}.txt");
        try
        {
            var tool = new WriteTool(NullLogger<WriteTool>.Instance);
            var args = JsonDocument.Parse($"{{\"path\": \"{tempFile.Replace("\\", "\\\\")}\", \"content\": \"first\"}}").RootElement;

            var result = await tool.ExecuteAsync(args, TestAgents.CreateToolContext());

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(result.Effect).IsEqualTo(ToolResultEffect.FileChange);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public async Task EditTool_Success_DeclaresFileChange()
    {
        string tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, "Hello");
            var tool = new EditTool(NullLogger<EditTool>.Instance);
            var args = JsonDocument.Parse(
                $"{{\"path\": \"{tempFile.Replace("\\", "\\\\")}\", \"oldString\": \"Hello\", \"newString\": \"Harbor\"}}").RootElement;

            var result = await tool.ExecuteAsync(args, TestAgents.CreateToolContext());

            await Assert.That(result.IsError).IsFalse();
            await Assert.That(result.Effect).IsEqualTo(ToolResultEffect.FileChange);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
