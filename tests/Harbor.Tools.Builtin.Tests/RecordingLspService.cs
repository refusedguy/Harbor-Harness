using Harbor.Abstractions.Lsp;
using Microsoft.Extensions.DependencyInjection;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>Recording <see cref="ILspService" /> stub for read/edit hook tests.</summary>
internal sealed class RecordingLspService : ILspService
{
    public List<string> Opened { get; } = [];
    public List<(string Path, string Text)> Changed { get; } = [];
    public List<LspDiagnostic> DiagnosticsToReturn { get; } = [];

    public event EventHandler<LspDiagnosticsChangedEventArgs>? DiagnosticsChanged;

    public bool SupportsFile(string filePath) =>
        filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
        || filePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase);

    public ValueTask OpenFileAsync(string filePath, string text, CancellationToken ct = default)
    {
        Opened.Add(filePath);
        return ValueTask.CompletedTask;
    }

    public ValueTask NotifyChangeAsync(string filePath, string newText, CancellationToken ct = default)
    {
        Changed.Add((filePath, newText));
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseFileAsync(string filePath) => ValueTask.CompletedTask;

    public ValueTask<IReadOnlyList<LspDiagnostic>> GetDiagnosticsAsync(string filePath, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<LspDiagnostic>>(DiagnosticsToReturn);

    public ValueTask<LspLocation?> FindDefinitionAsync(string filePath, int line, int column, CancellationToken ct = default) =>
        ValueTask.FromResult<LspLocation?>(null);

    public ValueTask<IReadOnlyList<LspLocation>> FindReferencesAsync(string filePath, int line, int column, CancellationToken ct = default) =>
        ValueTask.FromResult<IReadOnlyList<LspLocation>>([]);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static ServiceProvider ServicesWith(RecordingLspService lsp) =>
        new ServiceCollection().AddSingleton<ILspService>(lsp).BuildServiceProvider();
}
