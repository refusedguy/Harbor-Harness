using System.Text.Json;

namespace Harbor.Tui.CellForge.CodeGen.Automation;

/// <summary>
///     Error codes of the versioned automation protocol (issue #2438). The
///     method table uses them for every rejected request so a headless test
///     can branch on a stable code instead of parsing prose.
/// </summary>
public enum AutomationErrorCode
{
    /// <summary>No error.</summary>
    None = 0,

    /// <summary>A timed locator wait reached its virtual deadline.</summary>
    Timeout,

    /// <summary>No element matched the locator.</summary>
    NotFound,

    /// <summary>More than one element matched the locator.</summary>
    Ambiguous,

    /// <summary>The request, its method or its parameters were malformed.</summary>
    Protocol
}

/// <summary>Cell bounds of one element in the headless tree snapshot.</summary>
/// <param name="X">Column of the top-left corner.</param>
/// <param name="Y">Row of the top-left corner.</param>
/// <param name="Width">Width in cells.</param>
/// <param name="Height">Height in cells.</param>
public readonly record struct AutomationBounds(int X, int Y, int Width, int Height);

/// <summary>
///     One node of the tree returned by <c>getTree</c>. The shape is the
///     protocol contract: id, type, testId, bounds and children, in that
///     order. It is read from the parsed DSL tree, not from a test double.
/// </summary>
public sealed record AutomationNode(
    int Id,
    string Type,
    string? TestId,
    AutomationBounds Bounds,
    IReadOnlyList<AutomationNode> Children);

/// <summary>A protocol error attached to a correlated response.</summary>
public sealed record AutomationError(AutomationErrorCode Code, string Message);

/// <summary>A validated request decoded from one SSE <c>data:</c> line.</summary>
public sealed record AutomationRequest(string Id, string Method, JsonElement Parameters);

/// <summary>A response carrying the request id back to the caller.</summary>
public sealed record AutomationResponse(string Id, JsonElement? Result, AutomationError? Error);

/// <summary>Observable state of the virtual clock.</summary>
public sealed record AutomationClockState(long NowMilliseconds, bool IsPaused);
