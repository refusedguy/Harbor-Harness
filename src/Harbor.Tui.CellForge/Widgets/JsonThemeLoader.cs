using CSharpFunctionalExtensions;
using Harbor.DesignSystem;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.Services;

namespace Harbor.Tui.CellForge.Widgets;

public sealed class JsonThemeLoader : IThemeService
{
    public static HarborTheme Default { get; } = new HarborTheme(
        "harbor-terminal",
        Accent: new RgbColor(0x39, 0xBA, 0xE6),
        Success: new RgbColor(0x7F, 0xD9, 0x62),
        Warning: new RgbColor(0xFF, 0xB4, 0x54),
        Error: new RgbColor(0xFF, 0x6B, 0x6B),
        Tool: new RgbColor(0xD2, 0xA6, 0xFF),
        System: new RgbColor(0xF2, 0x96, 0x68),
        User: new RgbColor(0x39, 0xBA, 0xE6),
        Background: new RgbColor(0x0A, 0x0E, 0x14),
        Panel: new RgbColor(0x0D, 0x11, 0x17),
        Surface: new RgbColor(0x13, 0x18, 0x20),
        Surface2: new RgbColor(0x1A, 0x1F, 0x2B),
        Border: new RgbColor(0x1F, 0x24, 0x30),
        Muted: new RgbColor(0x5C, 0x67, 0x73),
        Text: new RgbColor(0xB3, 0xB9, 0xC5));

    public static RgbColor ChatUser => Default.Accent;
    public static RgbColor ChatAssistant => Default.Text;
    public static RgbColor ChatThinking => Default.Muted;
    public static RgbColor ChatTool => Default.Tool;
    public static RgbColor ChatToolResult => Default.Success;
    public static RgbColor ChatSystem => Default.System;
    public static RgbColor ChatError => Default.Error;
    public static RgbColor CostLow => Default.Success;
    public static RgbColor CostMid => Default.Warning;
    public static RgbColor CostHigh => Default.Error;

    public string Current => TerminalColorPalette.Current.Name;
    public bool IsDark => TerminalBackgroundProbe.RelativeLuminance(Default.Background) < TerminalBackgroundProbe.LightLuminanceThreshold;

    public void Apply(string theme) => throw new NotImplementedException();
    public void ApplyDark() => throw new NotImplementedException();
    public void ApplyLight() => throw new NotImplementedException();
    public void Toggle() => throw new NotImplementedException();
    public void ApplyHds(string theme) => throw new NotImplementedException();
    public void SetThemeVariant(bool isDark) => throw new NotImplementedException();

    public event EventHandler<string>? ThemeJsonApplied;

    public Result<string> LoadJson(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Result.Failure<string>($"theme file not found: {path}");
            }

            return Result.Success(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            return Result.Failure<string>($"theme load failed: {ex.Message}");
        }
    }

    public Result ApplyJson(string json)
    {
        var result = Parse(json);
        if (result.IsSuccess)
        {
            TerminalColorPalette.Apply(result.Value);
            ThemeJsonApplied?.Invoke(this, json);
        }

        return result.IsSuccess ? Result.Success() : Result.Failure(result.Error);
    }

    public static Result<HarborTheme> LoadFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Result.Failure<HarborTheme>($"theme file not found: {path}");
            }

            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            return Result.Failure<HarborTheme>($"theme load failed: {ex.Message}");
        }
    }

    public static Result<HarborTheme> Parse(string json)
    {
        var result = ThemeJson.Parse(json, TerminalColorPalette.Current);
        return result.IsSuccess
            ? Result.Success(result.Theme)
            : Result.Failure<HarborTheme>(result.Error);
    }

    public static Result<HarborTheme> Parse(string json, HarborTheme fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        var result = ThemeJson.Parse(json, fallback);
        return result.IsSuccess
            ? Result.Success(result.Theme)
            : Result.Failure<HarborTheme>(result.Error);
    }

    internal static bool TryParseHex(string hex, out RgbColor color) => ThemeJson.TryParseHex(hex, out color);

    public IDisposable Watch(string path)
    {
        var watcher = new ThemeFileWatcher(path, ApplyResult, OnError);
        return watcher;

        void ApplyResult(HarborTheme theme)
        {
            TerminalColorPalette.Apply(theme);
            ThemeJsonApplied?.Invoke(this, string.Empty);
        }

        void OnError(string error)
        {
            // Theme file watcher errors are non-fatal; live-reload resumes on next write.
        }
    }
}
