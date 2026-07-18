// hello.js — plain-JavaScript twin of hello.ts.
//
// Use this if you don't have `tsc` installed. The Harbor script loader
// evaluates .js files directly with Jint — no transpilation step.

Harbor.registerTool({
  name: "hello",
  displayName: "Hello",
  description: "Returns a greeting. Demonstrates a script-registered tool (JS).",
  parameterSchema: {
    type: "object",
    properties: {
      name: { type: "string", description: "Who to greet (default: 'world')" }
    }
  },
  executionMode: "Parallel",
  execute: (args) => {
    const name = (args && args.name) ? args.name : "world";
    return { output: `Hello, ${name}!`, isError: false };
  }
});

Harbor.log("hello tool registered (from .js)");
