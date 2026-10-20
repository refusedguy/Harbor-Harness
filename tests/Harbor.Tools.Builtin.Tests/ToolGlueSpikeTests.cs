using System.IO;
using Harbor.Abstractions.Tools;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     SG3 spike proof (issue 1244): the scalar ITool glue of every builtin tool
///     (except the dynamic <c>McpToolAdapter</c>) is emitted by
///     Harbor.CodeGen.ToolGlueGenerator from [ToolInfo] and the single-logger
///     constructors by AutoCtor — these tests pin the exact values the
///     deleted hand-written members used to carry, so a generator regression
///     fails here rather than in production output.
/// </summary>
public class ToolGlueSpikeTests
{
    [Test]
    public async Task Bash_GlueMatchesDeletedHandwrittenMembers()
    {
        var tool = new BashTool(new SystemProcessProxy(), NullLogger<BashTool>.Instance);

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

    [Test]
    public async Task Read_GlueMatchesDeletedHandwrittenMembers()
    {
        var tool = new ReadTool(NullLogger<ReadTool>.Instance);

        await Assert.That(tool.Name.Value).IsEqualTo("read");
        await Assert.That(tool.DisplayName).IsEqualTo("Read");
        await Assert.That(tool.Glyph).IsEqualTo("▸");
        await Assert.That(tool.Description).IsEqualTo(
            "Read a file. Text: returns numbered lines (use offset/limit for large files). Images: returns path/mime/size (vision payload depends on host wiring).");
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Parallel);
        await Assert.That(tool.PromptSnippet).IsEqualTo("read: Read file contents (text or image)");
        await Assert.That(tool.PromptGuidelines.Count).IsEqualTo(4);
        await Assert.That(tool.PromptGuidelines[0]).IsEqualTo("Use `read` before editing");
    }

    [Test]
    public async Task Edit_GlueMatchesDeletedHandwrittenMembers()
    {
        var tool = new EditTool(NullLogger<EditTool>.Instance);

        await Assert.That(tool.Name.Value).IsEqualTo("edit");
        await Assert.That(tool.DisplayName).IsEqualTo("Edit");
        await Assert.That(tool.Glyph).IsEqualTo("✎");
        await Assert.That(tool.Description).IsEqualTo(
            "Replace text in a file. Single: oldString→newString. Multi: edits[]. oldString must be unique unless replaceAll=true.");
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Sequential);
        await Assert.That(tool.PromptSnippet).IsEqualTo("edit: String replacement in a file");
        await Assert.That(tool.PromptGuidelines.Count).IsEqualTo(5);
        await Assert.That(tool.PromptGuidelines[0]).IsEqualTo("Prefer `edit` over `write` for existing files");
        await Assert.That(tool.PromptGuidelines[4]).Contains("expectedVersion");
    }

    [Test]
    public async Task Write_GlueMatchesDeletedHandwrittenMembers()
    {
        var tool = new WriteTool(NullLogger<WriteTool>.Instance);

        await Assert.That(tool.Name.Value).IsEqualTo("write");
        await Assert.That(tool.DisplayName).IsEqualTo("Write");
        await Assert.That(tool.Glyph).IsEqualTo("✚");
        await Assert.That(tool.Description).IsEqualTo(
            "Write content to a file (create or overwrite). Creates parent directories by default.");
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Sequential);
        await Assert.That(tool.PromptSnippet).IsEqualTo("write: Write file contents");
        await Assert.That(tool.PromptGuidelines.Count).IsEqualTo(4);
        await Assert.That(tool.PromptGuidelines[1]).IsEqualTo("For small changes prefer `edit` after `read`");
        await Assert.That(tool.PromptGuidelines[3]).Contains("expectedVersion");
    }

    [Test]
    public async Task Patch_GlueMatchesDeletedHandwrittenMembers()
    {
        var tool = new PatchTool(NullLogger<PatchTool>.Instance);

        await Assert.That(tool.Name.Value).IsEqualTo("patch");
        await Assert.That(tool.DisplayName).IsEqualTo("Patch");
        await Assert.That(tool.Glyph).IsEqualTo("⌥");
        await Assert.That(tool.Description).IsEqualTo(
            "Apply a unified-diff patch to a file. Context lines must match exactly. Atomic: writes a temp file then renames. Returns a compact preview of changes.");
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Sequential);
        await Assert.That(tool.PromptSnippet).IsEqualTo("patch: Apply a unified-diff patch to a file");
        await Assert.That(tool.PromptGuidelines.Count).IsEqualTo(4);
        await Assert.That(tool.PromptGuidelines[3]).IsEqualTo(
            "Hunks with mismatched context are rejected — the file is left untouched");
    }

    [Test]
    public async Task Ls_GlueMatchesDeletedHandwrittenMembers()
    {
        var tool = new LsTool(NullLogger<LsTool>.Instance);

        await Assert.That(tool.Name.Value).IsEqualTo("ls");
        await Assert.That(tool.DisplayName).IsEqualTo("List");
        await Assert.That(tool.Glyph).IsEqualTo("📁");
        await Assert.That(tool.Description).IsEqualTo(
            "List directory contents. Entries: [dir]/[file] with size and modified time for files. Recursive mode prunes VCS/build folders and caps total entries.");
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Parallel);
        await Assert.That(tool.PromptSnippet).IsEqualTo("ls: List directory contents");
        await Assert.That(tool.PromptGuidelines.Count).IsEqualTo(4);
        await Assert.That(tool.PromptGuidelines[0]).IsEqualTo(
            "Use `ls` to explore directory structure before reading files");
    }

    [Test]
    public async Task Tree_GlueMatchesDeletedHandwrittenMembers()
    {
        var tool = new TreeTool(NullLogger<TreeTool>.Instance);

        await Assert.That(tool.Name.Value).IsEqualTo("tree");
        await Assert.That(tool.DisplayName).IsEqualTo("Tree");
        await Assert.That(tool.Glyph).IsEqualTo(ToolGlyphs.Default);
        await Assert.That(tool.Description).IsEqualTo(
            "Render an ASCII directory tree. Respects .gitignore when git is available (else prunes common build/VCS dirs). Caps depth (default 3) and entries (default 1000).");
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Parallel);
        await Assert.That(tool.PromptSnippet).IsEqualTo("tree: ASCII directory tree (respects .gitignore)");
        await Assert.That(tool.PromptGuidelines.Count).IsEqualTo(4);
        await Assert.That(tool.PromptGuidelines[3]).IsEqualTo(
            "Use `ls` for one-level directory listing with sizes and mtimes");
    }

    [Test]
    public async Task RipGrep_GlueMatchesDeletedHandwrittenMembers()
    {
        var tool = new RipGrepTool(NullLogger<RipGrepTool>.Instance);

        await Assert.That(tool.Name.Value).IsEqualTo("ripgrep");
        await Assert.That(tool.DisplayName).IsEqualTo("RipGrep");
        await Assert.That(tool.Glyph).IsEqualTo(ToolGlyphs.Default);
        await Assert.That(tool.Description).IsEqualTo(
            "Fast recursive content search via the `rg` binary. Returns file:line: match content. Respects .gitignore automatically. Falls back to an error message hinting at `grep` if `rg` is not installed.");
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Parallel);
        await Assert.That(tool.PromptSnippet).IsEqualTo("ripgrep: Fast content search via ripgrep (rg)");
        await Assert.That(tool.PromptGuidelines.Count).IsEqualTo(4);
        await Assert.That(tool.PromptGuidelines[0]).IsEqualTo(
            "Use `ripgrep` (rg) for large-repo searches — much faster than `grep`");
    }

    [Test]
    public async Task Lsp_GlueMatchesDeletedHandwrittenMembers()
    {
        var tool = new LspTool(NullLogger<LspTool>.Instance);

        await Assert.That(tool.Name.Value).IsEqualTo("lsp");
        await Assert.That(tool.DisplayName).IsEqualTo("LSP");
        await Assert.That(tool.Glyph).IsEqualTo(ToolGlyphs.Default);
        await Assert.That(tool.Description).IsEqualTo(
            "Language-server intelligence for TypeScript, Python, Go, Rust, C#, C/C++, Java, HTML, CSS, JSON and Lua files. Actions: 'diagnostics' (errors/warnings for a file), 'definition' (go-to-definition), 'references' (find all usages). Lines are 1-based (matching read tool output), columns 0-based. A file should be opened (read) first so the language server sees its content.");
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Sequential);
        await Assert.That(tool.PromptSnippet).IsEqualTo("lsp: diagnostics / definition / references via language servers");
        await Assert.That(tool.PromptGuidelines.Count).IsEqualTo(4);
        await Assert.That(tool.PromptGuidelines[1]).IsEqualTo(
            "Use action 'definition' to jump to where a symbol is declared, 'references' to find usages");
    }

    [Test]
    public async Task WebFetch_GlueMatchesDeletedHandwrittenMembers()
    {
        var tool = new WebFetchTool(NullLogger<WebFetchTool>.Instance);

        await Assert.That(tool.Name.Value).IsEqualTo("webfetch");
        await Assert.That(tool.DisplayName).IsEqualTo("WebFetch");
        await Assert.That(tool.Glyph).IsEqualTo("🌍");
        await Assert.That(tool.Description).IsEqualTo(
            "Fetch a URL and return markdown-converted content. HTML is stripped to text + links + code; binary responses are rejected. Use for documentation, search results, API endpoints returning text.");
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Parallel);
        await Assert.That(tool.PromptSnippet).IsEqualTo("webfetch: Fetch a URL and return markdown");
        await Assert.That(tool.PromptGuidelines.Count).IsEqualTo(4);
        await Assert.That(tool.PromptGuidelines[3]).IsEqualTo("Binary and >5 MiB responses are rejected");
    }

    [Test]
    public async Task Skill_GlueMatchesDeletedHandwrittenMembers()
    {
        var tool = new SkillTool(NullLogger<SkillTool>.Instance);

        await Assert.That(tool.Name.Value).IsEqualTo("skill");
        await Assert.That(tool.DisplayName).IsEqualTo("Skill");
        await Assert.That(tool.Glyph).IsEqualTo(ToolGlyphs.Default);
        await Assert.That(tool.Description).IsEqualTo(
            "Load a SKILL.md skill body by name. Skills come from .harbor/skills/ (project) and ~/.harbor/skills/ (global); the available names are listed in the system prompt. Use this to follow a skill's workflow before doing the task it describes.");
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Parallel);
        await Assert.That(tool.PromptSnippet).IsEqualTo("skill: Load a SKILL.md skill body by name");
        await Assert.That(tool.PromptGuidelines.Count).IsEqualTo(3);
        await Assert.That(tool.PromptGuidelines[1]).IsEqualTo("Project skills shadow same-named global skills");
    }

    [Test]
    public async Task Mcp_GlueMatchesDeletedHandwrittenMembers()
    {
        var tool = new McpToolTool(NullLogger<McpToolTool>.Instance);

        await Assert.That(tool.Name.Value).IsEqualTo("mcp");
        await Assert.That(tool.DisplayName).IsEqualTo("MCP");
        await Assert.That(tool.Glyph).IsEqualTo(ToolGlyphs.Default);
        await Assert.That(tool.Description).IsEqualTo(
            "Invoke a method on a registered Model Context Protocol server. Servers are registered via IMcpRegistry.Register(name, stdioCmd). Common methods: tools/list, tools/call, resources/list, prompts/list.");
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Sequential);
        await Assert.That(tool.PromptSnippet).IsEqualTo("mcp: Invoke a method on a registered MCP server");
        await Assert.That(tool.PromptGuidelines.Count).IsEqualTo(4);
        await Assert.That(tool.PromptGuidelines[3]).IsEqualTo(
            "Result is the server's JSON response serialized to a string");
    }

    [Test]
    public async Task McpPrompt_GlueMatchesDeletedHandwrittenMembers()
    {
        var tool = new McpPromptTool(NullLogger<McpPromptTool>.Instance);

        await Assert.That(tool.Name.Value).IsEqualTo("mcp_prompt");
        await Assert.That(tool.DisplayName).IsEqualTo("MCP Prompt");
        await Assert.That(tool.Glyph).IsEqualTo(ToolGlyphs.Default);
        await Assert.That(tool.Description).IsEqualTo(
            "Render a prompt from a registered Model Context Protocol server. List available prompts first with the mcp tool (method 'prompts/list'). Returns the prompt messages as plain text to follow.");
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Parallel);
        await Assert.That(tool.PromptSnippet).IsEqualTo("mcp_prompt: Render an MCP server prompt by name");
        await Assert.That(tool.PromptGuidelines.Count).IsEqualTo(3);
        await Assert.That(tool.PromptGuidelines[0]).IsEqualTo(
            "Use `mcp_prompt` to expand a server-side prompt template (review checklist, commit message, …)");
    }

    [Test]
    public async Task McpResource_GlueMatchesDeletedHandwrittenMembers()
    {
        var tool = new McpResourceTool(NullLogger<McpResourceTool>.Instance);

        await Assert.That(tool.Name.Value).IsEqualTo("read_mcp_resource");
        await Assert.That(tool.DisplayName).IsEqualTo("MCP Resource");
        await Assert.That(tool.Glyph).IsEqualTo(ToolGlyphs.Default);
        await Assert.That(tool.Description).IsEqualTo(
            "Read a resource from a registered Model Context Protocol server by URI. List available URIs first with the mcp tool (method 'resources/list'). Returns the resource text contents.");
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Parallel);
        await Assert.That(tool.PromptSnippet).IsEqualTo("read_mcp_resource: Read an MCP server resource by URI");
        await Assert.That(tool.PromptGuidelines.Count).IsEqualTo(3);
        await Assert.That(tool.PromptGuidelines[2]).IsEqualTo(
            "Binary (base64 blob) resources are rejected — ask for a text view instead");
    }

    [Test]
    public async Task Notebook_GlueMatchesDeletedHandwrittenMembers()
    {
        var tool = new NotebookTool(NullLogger<NotebookTool>.Instance, Path.GetTempPath());

        await Assert.That(tool.Name.Value).IsEqualTo("notebook");
        await Assert.That(tool.DisplayName).IsEqualTo("Notebook");
        await Assert.That(tool.Glyph).IsEqualTo(ToolGlyphs.Default);
        await Assert.That(tool.Description).IsEqualTo(
            "Persistent markdown notes keyed by string. Actions: get/set/add/clear/list. Stored per session at ~/.harbor/notes/<sessionId>.json. Use for decisions, file lists, intermediate findings across long tasks.");
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Sequential);
        await Assert.That(tool.PromptSnippet).IsEqualTo("notebook: Persistent per-session notes (get/set/add/clear/list)");
        await Assert.That(tool.PromptGuidelines.Count).IsEqualTo(5);
        await Assert.That(tool.PromptGuidelines[4]).IsEqualTo(
            "Notes are scoped to the current session id — they don't leak across sessions");
    }

    [Test]
    public async Task SessionRead_GlueMatchesDeletedHandwrittenMembers()
    {
        var tool = new SessionReadTool(new FakeSessionStore(), NullLogger<SessionReadTool>.Instance);

        await Assert.That(tool.Name.Value).IsEqualTo("session_read");
        await Assert.That(tool.DisplayName).IsEqualTo("Session Read");
        await Assert.That(tool.Glyph).IsEqualTo(ToolGlyphs.Default);
        await Assert.That(tool.Description).IsEqualTo(
            "Inspect a peer session: its status, terminal run outcome, and recent transcript. Use it to check whether a neighboring session is ok, stuck, or failed before steering it. Read-only: never mutates the target session.");
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Parallel);
        await Assert.That(tool.PromptSnippet).IsEqualTo(
            "session_read: inspect a peer session's status, outcome and recent transcript");
        await Assert.That(tool.PromptGuidelines.Count).IsEqualTo(3);
        await Assert.That(tool.PromptGuidelines[2]).IsEqualTo(
            "verdict first (ok/stuck/failed), steer only when needed");
    }

    [Test]
    public async Task SessionSteer_GlueMatchesDeletedHandwrittenMembers()
    {
        var tool = new SessionSteerTool(new FakeSessionStore(), NullLogger<SessionSteerTool>.Instance);

        await Assert.That(tool.Name.Value).IsEqualTo("session_steer");
        await Assert.That(tool.DisplayName).IsEqualTo("Session Steer");
        await Assert.That(tool.Glyph).IsEqualTo(ToolGlyphs.Default);
        await Assert.That(tool.Description).IsEqualTo(
            "Send a directive to a peer session: message (share context or a result), redirect (change its task), or restart (ask it to re-run with the same context). Requires user approval. The directive lands in the peer's history for its next run; a live run is never interrupted.");
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Sequential);
        await Assert.That(tool.PromptSnippet).IsEqualTo(
            "session_steer: send a message, redirect, or restart directive to a peer session (needs approval)");
        await Assert.That(tool.PromptGuidelines.Count).IsEqualTo(3);
        await Assert.That(tool.PromptGuidelines[0]).IsEqualTo(
            "read the peer with session_read first — steer only on stuck/failed verdict");
    }

    [Test]
    public async Task Task_GlueMatchesDeletedHandwrittenMembers()
    {
        var tool = new TaskTool(new Harbor.Registries.Agents.AgentRegistry(), NullLogger<TaskTool>.Instance);

        await Assert.That(tool.Name.Value).IsEqualTo("task");
        await Assert.That(tool.DisplayName).IsEqualTo("Task");
        await Assert.That(tool.Glyph).IsEqualTo("☐");
        await Assert.That(tool.Description).IsEqualTo(
            "Delegate a self-contained task to a sub-agent (explore, plan, or custom agents marked as sub-agents). The sub-agent runs in its own isolated session with its own context window and tool access; only its final answer is returned here. Use it for wide read-only reconnaissance or focused planning that would otherwise flood your context. The sub-agent cannot spawn further sub-agents. Long independent work: pass background=true and continue other work — the final report arrives as a follow-up message, no polling needed.");
        await Assert.That(tool.ExecutionMode).IsEqualTo(ExecutionMode.Sequential);
        await Assert.That(tool.PromptSnippet).IsEqualTo(
            "task: delegate a self-contained task to a sub-agent and receive its final report");
        await Assert.That(tool.PromptGuidelines.Count).IsEqualTo(4);
        await Assert.That(tool.PromptGuidelines[3]).IsEqualTo(
            "long independent recon: `background: true`, keep working — the report arrives as a follow-up message, do not poll");
    }
}
