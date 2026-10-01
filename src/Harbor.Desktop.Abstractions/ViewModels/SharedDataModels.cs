using Harbor.Desktop.Abstractions.Models;

namespace Harbor.Desktop.Abstractions.ViewModels;

// Framework-neutral data-holder classes extracted from the isolated WPF
// view-models in apps/Harbor.App.Wpf/ViewModels/*.
// These are the canonical shapes shared by every desktop shell (WPF,
// Avalonia, MAUI, Blazor). They intentionally carry NO WPF-specific types.
// vm-dedup canon (audit 27-G): platform VMs inherit/project these shapes
// (WPF TokenBar heirs); Desktop *Base VMs + TuiViewModels own behavior,
// Framework hosts TEA-projection VMs.
//
// One clause of that canon was withdrawn by #803: the diff shapes
// (DiffLineKind / DiffLineViewModel / DiffHunkViewModel) are gone, because
// their only heir is the WPF shell under contrib/, which CI does not build,
// and no shell in the compiled product ever used them. The canon said these
// are shared by "every desktop shell"; the ones that compile did not share
// them. See the Diff section below for the whole argument.

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
// The diff shapes that used to live here — DiffLineKind / DiffLineViewModel /
// DiffHunkViewModel — are GONE, and the reason is that the only consumer of
// them is not in this repository (#803).
//
// They were extracted here from the isolated WPF shell, and the WPF shell is
// the one caller: contrib/apps/Harbor.App.Wpf/ViewModels/DiffViewModel.cs and
// its Views/DiffView.xaml DataTemplate. Nothing under src/, apps/ or tests/
// ever constructed a DiffLineViewModel, read a LineBrushKey, or named a
// DiffHunkViewModel — verified by grep over all three, zero hits. So the
// desktop contract advertised three types that no desktop shell in the
// compiled product consumed, and the cheapest consumer of any of them was a
// file CI never builds.
//
// That mattered more than dead code usually does, because the enum was named
// DiffLineKind and so was the live one: src/Harbor.Ui.Framework.Rendering/
// Widgets/DiffBlock.cs:7, five members, Context/Add/Delete/HunkHeader/
// FileHeader. Two public enums, one simple name, two assemblies, and INCOMPATIBLE
// members — Add/Delete against Added/Removed. Only Context is spelled the same
// in both. A reader who internalised one reaches for a member the other does
// not have, the compiler accepts both, and the error names the wrong file.
// The live one is the one a diff view must use; this one was the decoy.
//
// The live vocabulary is the engine's, and
// DiffSurfaceNameCollisionRule (Harbor.Architecture.Tests) now holds that: the
// kind enums and their carriers are read out of the project that owns
// Rendering.Widgets.LineDiff, and no other project may declare one of those
// names. The two genuinely distinct vocabularies are LineDiffRowKind (a row
// the engine COMPUTED) and DiffLineKind (a line of a unified diff DOCUMENT,
// which is why it has hunk and file headers) — both in the engine's project,
// both earning the name, and neither mergeable with the other.
//
// Deleting rather than renaming is the honest call here and not a style
// preference: the type had no shape worth a second name, because the
// pre-#679 index-alignment rows it described are no longer produced by
// anything. LineDiff is the one diff algorithm (#694) and the desktop
// DiffViewModel now hands its rows to it. What is left of the old contract is
// three classes describing a result no code computes.
//
// If a WPF/MAUI/Blazor shell is ever revived under contrib/, the shapes come
// back from Rendering.Widgets.DiffLine rather than as a new vocabulary: that
// type already carries Kind, OldNo, NewNo and Text, and it is the one the
// guards know about.

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
