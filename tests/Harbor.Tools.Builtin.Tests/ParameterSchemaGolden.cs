using System;
using System.Collections.Generic;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     #1574 — the pinned parameter schema of every builtin tool. These are the
///     exact JSON documents the tools shipped before their schema moved from an
///     instance property to a process-wide static, copied verbatim so a schema
///     edit has to be a deliberate act in both places.
/// </summary>
/// <remarks>
///     The comparison in <see cref="ParameterSchemaCachingTests" /> normalises
///     whitespace before comparing, so this file is a shape snapshot, not a
///     byte snapshot of the raw literal.
/// </remarks>
internal static class ParameterSchemaGolden
{
    /// <summary>Tool key → the tool's parameter schema as JSON.</summary>
    internal static IReadOnlyDictionary<string, string> Schemas { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bash"] =
                """
                {
                  "type": "object",
                  "properties": {
                    "command": { "type": "string", "description": "Shell command to execute" },
                    "cwd": { "type": "string", "description": "Working directory (default: current)" },
                    "timeout": { "type": "integer", "description": "Timeout in seconds (default: 30, max: 600)" },
                    "env": { "type": "object", "description": "Additional environment variables" }
                  },
                  "required": ["command"]
                }
                """,

            ["edit"] =
                """
                {
                  "type": "object",
                  "properties": {
                    "path": { "type": "string", "description": "File path to edit" },
                    "oldString": { "type": "string", "description": "Exact text to find (unique unless replaceAll)" },
                    "newString": { "type": "string", "description": "Replacement (empty = delete)" },
                    "replaceAll": { "type": "boolean", "description": "Replace all occurrences (default: false)" },
                    "expectedVersion": { "type": "string", "description": "Version of the file as you last read it (the [file-version: sha256:…] token). The edit is rejected when the file changed on disk since" },
                    "edits": {
                      "type": "array",
                      "description": "Multiple edits applied in order",
                      "items": {
                        "type": "object",
                        "properties": {
                          "oldString": { "type": "string" },
                          "newString": { "type": "string" },
                          "replaceAll": { "type": "boolean" }
                        },
                        "required": ["oldString", "newString"]
                      }
                    }
                  },
                  "required": ["path"]
                }
                """,

            ["glob"] =
                """
                {
                  "type": "object",
                  "properties": {
                    "pattern": { "type": "string", "description": "Glob pattern (e.g. '**/*.cs')" },
                    "path": { "type": "string", "description": "Base directory (default: cwd)" },
                    "ignoreGitignore": {
                      "type": "boolean",
                      "description": "If true, do not prune node_modules/bin/obj/.git/… (default: false)"
                    },
                    "maxResults": { "type": "integer", "description": "Max paths (default: 1000)" }
                  },
                  "required": ["pattern"]
                }
                """,

            ["grep"] =
                """
                {
                  "type": "object",
                  "properties": {
                    "pattern":    { "type": "string",  "description": "Regular expression pattern" },
                    "path":       { "type": "string",  "description": "Base directory or file (default: cwd)" },
                    "include":    { "type": "string",  "description": "File name glob (e.g. '*.cs')" },
                    "ignoreCase": { "type": "boolean", "description": "Case-insensitive (default: false)" },
                    "maxResults": { "type": "integer", "description": "Max matches (default: 100)" }
                  },
                  "required": ["pattern"]
                }
                """,

            ["ls"] =
                """
                {
                  "type": "object",
                  "properties": {
                    "path":      { "type": "string",  "description": "Directory to list (default: cwd)" },
                    "all":       { "type": "boolean", "description": "Include hidden files (default: false)" },
                    "recursive": { "type": "boolean", "description": "List recursively (default: false)" },
                    "depth":     { "type": "integer", "description": "Max recursion depth when recursive (default: 3, max: 8)" },
                    "maxEntries":{ "type": "integer", "description": "Max entries to return (default: 500)" }
                  }
                }
                """,

            ["lsp"] =
                """
                {
                  "type": "object",
                  "properties": {
                    "action":  { "type": "string", "enum": ["diagnostics", "definition", "references"],
                                 "description": "Which LSP query to run" },
                    "path":    { "type": "string", "description": "File path (absolute or workspace-relative)" },
                    "line":    { "type": "integer", "description": "1-based line of the symbol (required for definition/references)" },
                    "column":  { "type": "integer", "description": "0-based column of the symbol (default 0)" }
                  },
                  "required": ["action", "path"]
                }
                """,

            ["mcp"] =
                """
                {
                  "type": "object",
                  "properties": {
                    "server":  { "type": "string", "description": "Registered MCP server name" },
                    "method":  { "type": "string", "description": "JSON-RPC method (e.g. 'tools/list', 'tools/call')" },
                    "args":    { "type": "object", "description": "Arguments object (e.g. {\"name\":\"read_file\",\"arguments\":{\"path\":\"/tmp\"}})" }
                  },
                  "required": ["server", "method"]
                }
                """,

            ["mcp_adapter"] =
                """
                {
                  "type": "object",
                  "properties": {
                    "arguments": { "type": "object", "description": "Tool arguments" }
                  }
                }
                """,

            ["mcp_prompt"] =
                """
                {
                  "type": "object",
                  "properties": {
                    "server":    { "type": "string", "description": "Registered MCP server name" },
                    "name":      { "type": "string", "description": "Prompt name from prompts/list" },
                    "arguments": { "type": "object", "description": "Template variables (optional)" }
                  },
                  "required": ["server", "name"]
                }
                """,

            ["notebook"] =
                """
                {
                  "type": "object",
                  "properties": {
                    "action":  { "type": "string", "description": "One of: get | set | add | clear | list" },
                    "key":     { "type": "string", "description": "Note key (required for get/set/add/clear)" },
                    "content": { "type": "string", "description": "Note content (required for set; appended for add)" }
                  },
                  "required": ["action"]
                }
                """,

            ["patch"] =
                """
                {
                  "type": "object",
                  "properties": {
                    "path":  { "type": "string", "description": "File to patch" },
                    "patch": { "type": "string", "description": "Unified diff (one or more hunks starting with @@ ... @@)" }
                  },
                  "required": ["path", "patch"]
                }
                """,

            ["read"] =
                """
                {
                  "type": "object",
                  "properties": {
                    "path":   { "type": "string",  "description": "Absolute or relative file path" },
                    "offset": { "type": "integer", "description": "1-based start line (optional)" },
                    "limit":  { "type": "integer", "description": "Max lines to return (optional)" }
                  },
                  "required": ["path"]
                }
                """,

            ["read_mcp_resource"] =
                """
                {
                  "type": "object",
                  "properties": {
                    "server": { "type": "string", "description": "Registered MCP server name" },
                    "uri":    { "type": "string", "description": "Resource URI from resources/list (e.g. 'file:///docs/api.md')" }
                  },
                  "required": ["server", "uri"]
                }
                """,

            ["ripgrep"] =
                """
                {
                  "type": "object",
                  "properties": {
                    "pattern":    { "type": "string",  "description": "Search pattern (regex by default)" },
                    "path":       { "type": "string",  "description": "Base directory or file (default: cwd)" },
                    "glob":       { "type": "string",  "description": "File name glob (e.g. '*.cs' or '*.{cs,ts}')" },
                    "ignoreCase": { "type": "boolean", "description": "Case-insensitive (default: false)" },
                    "regex":      { "type": "boolean", "description": "Treat pattern as regex (default: true). false = fixed-string" },
                    "maxResults": { "type": "integer", "description": "Max matches (default: 100, max: 10000)" }
                  },
                  "required": ["pattern"]
                }
                """,

            ["session_read"] =
                """
                {
                  "type": "object",
                  "properties": {
                    "id": {
                      "type": "string",
                      "description": "Id of the peer session to inspect"
                    },
                    "limit": {
                      "type": "integer",
                      "description": "Max transcript messages to return (default 20, max 50)"
                    },
                    "include_transcript": {
                      "type": "boolean",
                      "description": "Include the recent transcript (default true)"
                    }
                  },
                  "required": ["id"]
                }
                """,

            ["session_steer"] =
                """
                {
                  "type": "object",
                  "properties": {
                    "id": {
                      "type": "string",
                      "description": "Id of the peer session to steer"
                    },
                    "instruction": {
                      "type": "string",
                      "description": "The directive for the peer session"
                    },
                    "operation": {
                      "type": "string",
                      "enum": ["message", "redirect", "restart"],
                      "description": "Peer operation (default message)"
                    }
                  },
                  "required": ["id", "instruction"]
                }
                """,

            ["skill"] =
                """
                {
                  "type": "object",
                  "properties": {
                    "name":  { "type": "string", "description": "Skill name as listed in <available_skills> (e.g. 'code-review')" },
                    "scope": { "type": "string", "description": "Where to look: 'project', 'global', or 'any' (default)" }
                  },
                  "required": ["name"]
                }
                """,

            ["task"] =
                """
                {
                  "type": "object",
                  "properties": {
                    "agent": {
                      "type": "string",
                      "description": "Name of the sub-agent to use (e.g. 'explore', 'plan')"
                    },
                    "prompt": {
                      "type": "string",
                      "description": "Task description for the sub-agent. Should be self-contained."
                    },
                    "background": {
                      "type": "boolean",
                      "description": "Run detached and return immediately with a handle (task_N). The final report arrives as a follow-up message; continue other work meanwhile."
                      }
                  },
                  "required": ["agent", "prompt"]
                }
                """,

            ["tree"] =
                """
                {
                  "type": "object",
                  "properties": {
                    "path":           { "type": "string",  "description": "Directory to tree (default: cwd)" },
                    "maxDepth":       { "type": "integer", "description": "Max depth (default: 3, max: 10)" },
                    "includeHidden":  { "type": "boolean", "description": "Include hidden files (default: false)" },
                    "gitignore":      { "type": "boolean", "description": "Respect .gitignore via git ls-files (default: true)" },
                    "maxEntries":     { "type": "integer", "description": "Max entries (default: 1000, max: 10000)" }
                  }
                }
                """,

            ["webfetch"] =
                """
                {
                  "type": "object",
                  "properties": {
                    "url":       { "type": "string",  "description": "Absolute URL (http or https)" },
                    "selector":  { "type": "string",  "description": "Optional CSS-like selector to extract a subtree (e.g. 'main', 'article', '#content', '.docs')" },
                    "maxChars":  { "type": "integer", "description": "Max characters of markdown to return (default: 50000, max: 500000)" }
                  },
                  "required": ["url"]
                }
                """,

            ["write"] =
                """
                {
                  "type": "object",
                  "properties": {
                    "path":       { "type": "string",  "description": "File path to write" },
                    "content":    { "type": "string",  "description": "Full file content" },
                    "createDirs": { "type": "boolean", "description": "Create parent dirs (default: true)" },
                    "expectedVersion": { "type": "string", "description": "Version of the file as you last read it (the [file-version: sha256:…] token). The write is rejected when the file changed on disk since" }
                  },
                  "required": ["path", "content"]
                }
                """,
        };
}
