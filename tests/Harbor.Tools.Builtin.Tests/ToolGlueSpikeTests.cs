using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     SG3 spike proof (issue 1244): the scalar ITool glue of Bash/Glob/Grep
///     is emitted by Harbor.CodeGen.ToolGlueGenerator from [ToolInfo] and the
///     constructors by AutoCtor — these tests pin the exact values the
///     deleted hand-written members used to carry, so a generator regression
///     fails here rather than in production output.
/// </summary>
public class ToolGlueSpikeTests
{
    [Test]
    public async Task Bash_GlueMatchesDeletedHandwrittenMembers()
    {
        var tool = new BashTool(NullLogger<BashTool>.Instance);

        await Assert.That(tool.Name.Value).IsEqualTo("bash");
        await Assert.That(tool.DisplayName).IsEqualTo("Bash");
        await Assert.That(tool.Glyph).IsEqualTo("$");
        await Assert.That(tool.Description).IsEqualTo(
            "Execute a shell command. Output is captured and returned. Commands run in the current working directory. Use `cwd` to override.");
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Sequential);
        await Assert.That(tool.PromptSnippet).IsEqualTo("bash: Execute shell commands");
        await Assert.That(tool.PromptGuidelines.Count).IsEqualTo(3);
        await Assert.That(tool.PromptGuidelines[0]).IsEqualTo(
            "Prefer dedicated tools (read, edit, glob, grep) for file operations");
    }

    [Test]
    public async Task Glob_GlueMatchesDeletedHandwrittenMembers()
    {
        var tool = new GlobTool(NullLogger<GlobTool>.Instance);

        await Assert.That(tool.Name.Value).IsEqualTo("glob");
        await Assert.That(tool.DisplayName).IsEqualTo("Glob");
        await Assert.That(tool.Glyph).IsEqualTo("🌐");
        await Assert.That(tool.Description).IsEqualTo(
            "Find files matching a glob (e.g. **/*.cs, src/**/*.ts). Returns relative paths. Prunes VCS/build folders by default (not a full .gitignore implementation).");
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Parallel);
        await Assert.That(tool.PromptSnippet).IsEqualTo("glob: Find files by pattern");
        await Assert.That(tool.PromptGuidelines.Count).IsEqualTo(4);
        await Assert.That(tool.PromptGuidelines[3]).IsEqualTo(
            "Set ignoreGitignore=true to also search node_modules/bin/obj/etc.");
    }

    [Test]
    public async Task Grep_GlueMatchesDeletedHandwrittenMembers()
    {
        var tool = new GrepTool(NullLogger<GrepTool>.Instance);

        await Assert.That(tool.Name.Value).IsEqualTo("grep");
        await Assert.That(tool.DisplayName).IsEqualTo("Grep");
        await Assert.That(tool.Glyph).IsEqualTo("🔍");
        await Assert.That(tool.Description).IsEqualTo(
            "Search file contents with regex. Returns matching lines as file:line:content. Recurses from path; skips VCS/build dirs and binary files.");
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Parallel);
        await Assert.That(tool.PromptSnippet).IsEqualTo("grep: Search file contents");
        await Assert.That(tool.PromptGuidelines.Count).IsEqualTo(3);
        await Assert.That(tool.PromptGuidelines[2]).IsEqualTo(
            "Use `include` to limit search (e.g. '*.cs' or '*.{cs,ts}')");
    }
}
