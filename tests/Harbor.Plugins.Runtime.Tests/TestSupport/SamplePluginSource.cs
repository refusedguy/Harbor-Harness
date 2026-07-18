namespace Harbor.Plugins.Runtime.Tests.TestSupport;

/// <summary>
///     Canonical hello-world CS-source plugin source generator. Suffix isolates types
///     per test run so the shared AppDomain doesn't see duplicate type names.
/// </summary>
public static class SamplePluginSource
{
    /// <summary>
    ///     Generate a hello-world CS-source plugin with the given unique suffix. The
    ///     suffix is appended to class and tool names so different test runs don't
    ///     collide in the shared AppDomain type system.
    /// </summary>
    public static string HelloWorld(string suffix) => $$"""
        using System;
        using System.Collections.Generic;
        using System.Text.Json;
        using System.Threading;
        using System.Threading.Tasks;
        using CSharpFunctionalExtensions;
        using Harbor.Abstractions.Models;
        using Harbor.Abstractions.Models.Identifiers;
        using Harbor.Abstractions.Plugins;
        using Harbor.Abstractions.Tools;
        using Microsoft.Extensions.Logging;

        public sealed class HelloPlugin{{suffix}} : IToolPlugin
        {
            public string Name => "hello-world-{{suffix.ToLowerInvariant()}}";
            public Version Version => new(1, 0, 0);
            public Version RequiredHarborVersion => new(0, 4, 0);
            public string Description => "Test plugin {{suffix}}";

            public void Initialize(PluginContext context)
            {
                context.CreateLogger<HelloPlugin{{suffix}}>().LogInformation("{{suffix}} initialized");
            }

            public void RegisterTools(IToolRegistryBuilder builder) => builder.AddTool<HelloTool{{suffix}}>();

            public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        }

        public sealed class HelloTool{{suffix}} : ITool
        {
            public ToolName Name => ToolName.Create("hello_{{suffix.ToLowerInvariant()}}");
            public string DisplayName => "Hello {{suffix}}";
            public string Description => "Returns a greeting";
            public JsonDocument ParameterSchema => JsonDocument.Parse("{\"type\":\"object\"}");
            public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
            public string? PromptSnippet => null;
            public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

            public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(ToolResult.Success("Hello from {{suffix}}!"));
            }
        }
        """;

    /// <summary>A CS source file with a deliberate syntax error for compile-failure tests.</summary>
    public static string Broken() => """
        // This file is intentionally broken.
        public sealed class Broken {
            public void M() {
                int x = ; // syntax error — missing expression
            }
        }
        """;

    /// <summary>
    ///     A CS source file declaring a class that implements IPlugin but has NO
    ///     parameterless constructor — the instantiator should skip it.
    /// </summary>
    public static string NoParameterlessCtor(string suffix) => $$"""
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Harbor.Abstractions.Plugins;

        public sealed class CtorOnlyPlugin{{suffix}} : IPlugin
        {
            public CtorOnlyPlugin{{suffix}}(int _) { }
            public string Name => "ctor-only-{{suffix.ToLowerInvariant()}}";
            public Version Version => new(1, 0, 0);
            public Version RequiredHarborVersion => new(0, 4, 0);
            public string Description => "Has no parameterless ctor";
            public void Initialize(PluginContext context) { }
            public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
        """;
}
