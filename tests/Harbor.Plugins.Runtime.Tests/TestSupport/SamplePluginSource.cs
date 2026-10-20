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
    /// <param name="suffix">Unique per-run suffix for the generated type names.</param>
    /// <param name="requiredHarborVersion">
    ///     The <c>RequiredHarborVersion</c> the generated plugin declares, as the
    ///     argument list of a <c>Version</c> ("0, 4, 0"). Defaults to the Harbor
    ///     version this build runs (#1721's gate compares against it).
    /// </param>
    public static string HelloWorld(string suffix, string requiredHarborVersion = "0, 4, 0") => $$"""
                                                        using System;
                                                        using System.Collections.Generic;
                                                        using System.Text.Json;
                                                        using System.Threading;
                                                        using System.Threading.Tasks;
                                                        using CSharpFunctionalExtensions;
                                                        using Harbor.Abstractions.Models;
                                                        using Harbor.Abstractions.Models.Identifiers;
                                                        using Harbor.Abstractions.Permissions;
                                                        using Harbor.Abstractions.Plugins;
                                                        using Harbor.Abstractions.Tools;
                                                        using Microsoft.Extensions.Logging;

                                                        public sealed class HelloPlugin{{suffix}} : IToolPlugin
                                                        {
                                                            public string Name => "hello-world-{{suffix.ToLowerInvariant()}}";
                                                            public Version Version => new(1, 0, 0);
                                                            public Version RequiredHarborVersion => new({{requiredHarborVersion}});
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
                                                            public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

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
    ///     A CS-source plugin with a chosen plugin name and a chosen set of DECLARED
    ///     DEPENDENCIES (#1733).
    /// </summary>
    /// <remarks>
    ///     It is a tool plugin, so a test can see whether it contributed anything by
    ///     looking at the tools the host registered: a plugin that is still waiting for a
    ///     dependency registers none.
    /// </remarks>
    /// <param name="suffix">Unique suffix, so two runs never collide in the shared AppDomain.</param>
    /// <param name="pluginName">The plugin's own name — the name another plugin can depend on.</param>
    /// <param name="dependencies">The dependency names the plugin declares.</param>
    public static string WithDependencies(string suffix, string pluginName, params string[] dependencies)
    {
        string declared = string.Join(", ", Array.ConvertAll(dependencies, static d => $"\"{d}\""));

        return $$"""
                using System;
                using System.Collections.Generic;
                using System.Text.Json;
                using System.Threading;
                using System.Threading.Tasks;
                using CSharpFunctionalExtensions;
                using Harbor.Abstractions.Models;
                using Harbor.Abstractions.Models.Identifiers;
                using Harbor.Abstractions.Permissions;
                using Harbor.Abstractions.Plugins;
                using Harbor.Abstractions.Tools;

                public sealed class DepPlugin{{suffix}} : IToolPlugin
                {
                    public string Name => "{{pluginName}}";
                    public Version Version => new(1, 0, 0);
                    public Version RequiredHarborVersion => new(0, 4, 0);
                    public string Description => "Test plugin {{suffix}}";
                    public IReadOnlyList<string> Dependencies => new string[] { {{declared}} };

                    public void Initialize(PluginContext context) { }

                    public void RegisterTools(IToolRegistryBuilder builder) => builder.AddTool<DepTool{{suffix}}>();

                    public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
                }

                public sealed class DepTool{{suffix}} : ITool
                {
                    public ToolName Name => ToolName.Create("dep_{{suffix.ToLowerInvariant()}}");
                    public string DisplayName => "Dep {{suffix}}";
                    public string Description => "Returns a greeting";
                    public JsonDocument ParameterSchema => JsonDocument.Parse("{\"type\":\"object\"}");
                    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
                    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

                    public string? PromptSnippet => null;
                    public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

                    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken)
                    {
                        return Task.FromResult(ToolResult.Success("Hello from {{suffix}}!"));
                    }
                }
                """;
    }

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
