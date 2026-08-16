using Harbor.Desktop.Abstractions.Models;

namespace Harbor.Desktop.Abstractions.ViewModels;

// Framework-neutral data-holder classes extracted from the isolated WPF
// view-models in apps/Harbor.App.Wpf/ViewModels/*.
// These are the canonical shapes shared by every desktop shell (WPF,
// Avalonia, MAUI, Blazor). They intentionally carry NO WPF-specific types.

// ─────────────────────────────────────────────────────────────────────────────
// Chat
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>One chat transcript line.</summary>
public class ChatMessageViewModel
{
    public string Role { get; init; }
    public string Content { get; init; }
    public DateTimeOffset Timestamp { get; init; }

    public ChatMessageViewModel(string role, string content, DateTimeOffset timestamp)
    {
        Role = role;
        Content = content;
        Timestamp = timestamp;
    }

    public string DisplayTime => Timestamp.ToLocalTime().ToString("HH:mm");
    public bool IsUser => Role == "user";
    public string RoleBrushKey => Role switch
    {
        "user" => "ChatUserBrush",
        "assistant" => "ChatAssistantBrush",
        "tool" => "ChatToolBrush",
        "tool_result" => "ChatToolResultBrush",
        "error" => "ChatErrorBrush",
        _ => "ChatAssistantBrush"
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// Sessions
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Sidebar entry for a session.</summary>
public class SessionEntryViewModel
{
    public string Id { get; init; }
    public string Title { get; init; }
    public string AgentName { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string? ParentId { get; init; }

    public SessionEntryViewModel(string id, string title, string agentName, DateTimeOffset updatedAt, string? parentId)
    {
        Id = id;
        Title = title;
        AgentName = agentName;
        UpdatedAt = updatedAt;
        ParentId = parentId;
    }

    public string DisplayTime => UpdatedAt.ToLocalTime().ToString("MM-dd HH:mm");
    public bool IsFork => ParentId is not null;
    public string Badge => IsFork ? "⑂" : AgentName[..1];
}

// ─────────────────────────────────────────────────────────────────────────────
// Providers
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>One provider in the browser.</summary>
public class ProviderEntryViewModel
{
    public string Id { get; init; }
    public string DisplayName { get; init; }
    public string Description { get; init; }
    public IReadOnlyList<ModelEntryViewModel> Models { get; init; }

    public ProviderEntryViewModel(string id, string displayName, string description, IReadOnlyList<ModelEntryViewModel> models)
    {
        Id = id;
        DisplayName = displayName;
        Description = description;
        Models = models;
    }
}

/// <summary>One model offered by a provider.</summary>
public class ModelEntryViewModel
{
    public string Id { get; init; }
    public string DisplayName { get; init; }
    public int ContextWindow { get; init; }
    public int MaxOutputTokens { get; init; }
    public bool SupportsVision { get; init; }
    public bool SupportsTools { get; init; }

    public ModelEntryViewModel(string id, string displayName, int contextWindow, int maxOutputTokens, bool supportsVision, bool supportsTools)
    {
        Id = id;
        DisplayName = displayName;
        ContextWindow = contextWindow;
        MaxOutputTokens = maxOutputTokens;
        SupportsVision = supportsVision;
        SupportsTools = supportsTools;
    }

    public string Summary =>
        $"{ContextWindow / 1000}K ctx · {MaxOutputTokens / 1000}K out" +
        (SupportsVision ? " · vision" : "") +
        (SupportsTools ? " · tools" : "");
}

// ─────────────────────────────────────────────────────────────────────────────
// Command palette
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>A command entry in the palette.</summary>
public class CommandEntry
{
    public string Id { get; init; }
    public string Title { get; init; }
    public string Description { get; init; }
    public string Category { get; init; }

    public CommandEntry(string id, string title, string description, string category)
    {
        Id = id;
        Title = title;
        Description = description;
        Category = category;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Diff
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Kind of diff line.</summary>
public enum DiffLineKind
{
    Context,
    Added,
    Removed
}

/// <summary>A single diff line.</summary>
public class DiffLineViewModel
{
    public string Text { get; init; }
    public DiffLineKind Kind { get; init; }

    public DiffLineViewModel(string text, DiffLineKind kind)
    {
        Text = text;
        Kind = kind;
    }

    public string LineBrushKey => Kind switch
    {
        DiffLineKind.Added => "DiffAddedBrush",
        DiffLineKind.Removed => "DiffRemovedBrush",
        _ => "DiffContextBrush"
    };
}

/// <summary>A single diff hunk.</summary>
public class DiffHunkViewModel
{
    public string Header { get; init; }
    public IReadOnlyList<DiffLineViewModel> Lines { get; init; }

    public DiffHunkViewModel(string header, IReadOnlyList<DiffLineViewModel> lines)
    {
        Header = header;
        Lines = lines;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Token usage
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>One bar in the token usage chart.</summary>
public class TokenBarViewModel
{
    public string Label { get; init; }
    public double InputHeight { get; init; }
    public double OutputHeight { get; init; }
    public string InputBrushKey { get; init; }
    public string OutputBrushKey { get; init; }

    public TokenBarViewModel(string label, double inputHeight, double outputHeight, string inputBrushKey = "", string outputBrushKey = "")
    {
        Label = label;
        InputHeight = inputHeight;
        OutputHeight = outputHeight;
        InputBrushKey = inputBrushKey;
        OutputBrushKey = outputBrushKey;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Toasts
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>A single toast notification.</summary>
public class ToastViewModel
{
    public string Id { get; init; }
    public string Message { get; init; }
    public Harbor.Desktop.Abstractions.Models.ToastKind Kind { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public TimeSpan TimeToLive { get; init; }

    public ToastViewModel(string id, string message, Harbor.Desktop.Abstractions.Models.ToastKind kind, DateTimeOffset createdAt, TimeSpan timeToLive)
    {
        Id = id;
        Message = message;
        Kind = kind;
        CreatedAt = createdAt;
        TimeToLive = timeToLive;
    }

    public string Icon => Kind switch
    {
        Harbor.Desktop.Abstractions.Models.ToastKind.Success => "✓",
        Harbor.Desktop.Abstractions.Models.ToastKind.Warning => "▲",
        Harbor.Desktop.Abstractions.Models.ToastKind.Error => "✕",
        _ => "ℹ"
    };
}
