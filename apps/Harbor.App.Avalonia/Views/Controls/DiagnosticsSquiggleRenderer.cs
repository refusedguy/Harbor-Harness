using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Harbor.Abstractions.Lsp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Avalonia.Views.Controls;

/// <summary>
///     AvaloniaEdit background renderer that draws colored underlines for the
///     published LSP diagnostics of the active editor tab (error red, warning
///     amber, info/hint blue). Colors resolve from the app ResourceDictionary
///     first (<c>LspErrorBrush</c>, <c>LspWarningBrush</c>, <c>LspInfoBrush</c>)
///     with fixed fallbacks — no C# token ownership (§What-not-to-do #9).
/// </summary>
public sealed class DiagnosticsSquiggleRenderer : IBackgroundRenderer
{
    private static readonly ILogger<DiagnosticsSquiggleRenderer> Logger =
        LoggerFactory.Create(b => b.AddDebug()).CreateLogger<DiagnosticsSquiggleRenderer>();

    private readonly TextView _textView;
    private IReadOnlyList<LspDiagnostic> _diagnostics = [];

    /// <summary>Underline layer sits with selection, under the caret.</summary>
    public KnownLayer Layer => KnownLayer.Selection;

    public DiagnosticsSquiggleRenderer(TextView textView)
    {
        _textView = textView;
    }

    /// <summary>Replace the rendered diagnostic set (already filtered to the active tab).</summary>
    public void SetDiagnostics(IReadOnlyList<LspDiagnostic> diagnostics)
    {
        _diagnostics = diagnostics ?? [];
        _textView.InvalidateLayer(Layer);
    }

    /// <inheritdoc />
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_diagnostics.Count == 0 || textView.Document is null) return;

        foreach (LspDiagnostic diagnostic in _diagnostics)
        {
            DrawUnderline(textView, drawingContext, diagnostic);
        }
    }

    private void DrawUnderline(TextView textView, DrawingContext context, LspDiagnostic diagnostic)
    {
        try
        {
            TextDocument document = textView.Document!;
            int startOffset = document.GetOffset(diagnostic.Line + 1, diagnostic.Column);
            int endOffset = document.GetOffset(diagnostic.EndLine + 1, diagnostic.EndColumn);
            if (endOffset < startOffset) return;

            DocumentLine startDocLine = document.GetLineByOffset(startOffset);
            VisualLine? startLine = textView.GetVisualLine(startDocLine.LineNumber);
            if (startLine is null || startLine.TextLines.Count == 0) return; // not realized on screen

            int startColumn = startLine.GetVisualColumn(startOffset - startDocLine.Offset);
            int endColumn;
            bool sameLine = document.GetLineByOffset(endOffset).LineNumber == startDocLine.LineNumber;
            if (sameLine)
            {
                endColumn = startLine.GetVisualColumn(endOffset - startDocLine.Offset);
            }
            else
            {
                endColumn = startLine.VisualLength;
            }

            TextLine? startTextLine = TextLineForColumn(startLine, startColumn);
            TextLine? endTextLine = sameLine ? TextLineForColumn(startLine, endColumn) : startLine.TextLines[^1];
            if (startTextLine is null || endTextLine is null) return;

            double x = startLine.GetTextLineVisualXPosition(startTextLine, startColumn);
            double xEnd = startLine.GetTextLineVisualXPosition(endTextLine, endColumn);
            double y = startLine.GetTextLineVisualYPosition(startTextLine, VisualYPosition.LineBottom) - 2;
            double width = Math.Max(xEnd - x, 4);

            context.FillRectangle(BrushFor(diagnostic.Severity), new Rect(x, y, width, 2));
        }
        catch (Exception ex)
        {
            // Rendering must never take the editor down: skip unrealized/malformed ranges.
            Logger.LogDebug(ex, "Squiggle draw skipped for {Path}:{Line}", diagnostic.FilePath, diagnostic.Line);
        }
    }

    private static TextLine? TextLineForColumn(VisualLine line, int column)
    {
        for (int i = 0; i < line.TextLines.Count; i++)
        {
            int start = line.GetTextLineVisualStartColumn(line.TextLines[i]);
            int end = i + 1 < line.TextLines.Count
                ? line.GetTextLineVisualStartColumn(line.TextLines[i + 1])
                : line.VisualLengthWithEndOfLineMarker + 1;
            if (column >= start && column < end) return line.TextLines[i];
        }

        return line.TextLines.Count > 0 ? line.TextLines[^1] : null;
    }

    private IBrush BrushFor(LspSeverity severity)
    {
        string key = severity switch
        {
            LspSeverity.Error => "LspErrorBrush",
            LspSeverity.Warning => "LspWarningBrush",
            _ => "LspInfoBrush",
        };

        if (_textView.TryGetResource(key, ThemeVariant.Default, out object? resource) && resource is IBrush brush)
        {
            return brush;
        }

        return severity switch
        {
            LspSeverity.Error => Brushes.Red,
            LspSeverity.Warning => Brushes.Orange,
            _ => Brushes.DodgerBlue,
        };
    }
}
