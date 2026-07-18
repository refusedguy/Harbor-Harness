// hello.ts — TypeScript plugin sample for Harbor.
//
// This file demonstrates the Harbor scripting model: a single .ts file in
// ~/.harbor/scripts/ registers a tool that becomes invocable by agents.
//
// To run:
//   1. Install TypeScript once:   npm i -g typescript
//   2. Drop this file into:       ~/.harbor/scripts/hello.ts
//   3. Start Harbor — the `hello` tool is auto-registered on startup.
//
// Or, equivalently, run it from the CLI:
//   harbor --script ./samples/scripts/hello.ts
//
// See docs/SCRIPTING.md for the comparison of CS (Roslyn), JS (Jint), TS
// (SharpTS / tsc+Jint), and MCP — and when to pick which.

interface HelloArgs {
  name?: string;
}

interface HelloResult {
  output: string;
  isError: boolean;
}

Harbor.registerTool({
  name: "hello",
  displayName: "Hello",
  description: "Returns a greeting. Demonstrates a script-registered tool.",
  parameterSchema: {
    type: "object",
    properties: {
      name: { type: "string", description: "Who to greet (default: 'world')" }
    }
  },
  executionMode: "Parallel",
  // NOTE: PoC supports synchronous `execute` only. The `async` keyword is
  // accepted by tsc but the resulting Promise is not drained — return a
  // plain object instead. See docs/SCRIPTING.md §Async limitation.
  execute: (args: HelloArgs): HelloResult => {
    const name: string = args?.name ?? "world";
    return { output: `Hello, ${name}!`, isError: false };
  }
});

Harbor.log("hello tool registered");

// You can also enumerate other tools/providers/agents visible to this script:
//   const tools = Harbor.tools.list();
//   Harbor.log(`Visible tools: ${tools.length}`);
