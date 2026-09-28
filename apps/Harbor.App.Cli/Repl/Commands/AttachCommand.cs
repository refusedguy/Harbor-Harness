using Harbor.Application.Attachments;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.App.Cli.Repl.Commands;

/// <summary>
///     <c>/attach &lt;path&gt;</c> (issue #386) — stage an image for the next
///     user turn. The file is read and validated HERE (magic bytes, size, and
///     whether the current model has vision at all) so a bad attachment is a
///     one-line timeline error, not a provider 400 halfway through a stream.
/// </summary>
internal sealed class AttachCommand : IReplCommand
{
    private readonly ImageAttachmentReader _reader;

    public AttachCommand(ImageAttachmentReader? reader = null) => _reader = reader ?? new ImageAttachmentReader();

    public string Id => "attach";
    public IReadOnlyList<string> Aliases => ["image"];
    public string Title => "Attach image";
    public string Description => $"attach an image to the next message ({ImageProbe.SupportedFormats})";
    public string Group => "Context";

    public Task ExecuteAsync(ReplCommandContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var host = ctx.Host;

        if (host.Attachments is not { } stash)
        {
            host.Bridge.AppendSystemLine("! /attach is not available in this build.");
            host.WakeUp();
            return Task.CompletedTask;
        }

        if (host.Agent.State.IsRunning)
        {
            host.Bridge.AppendSystemLine("⚠ Agent is busy — wait for completion or press Esc / Ctrl+C to abort.");
            host.WakeUp();
            return Task.CompletedTask;
        }

        if (stash.Count >= ImageAttachmentStash.MaxPending)
        {
            host.Bridge.AppendSystemLine(
                $"! Already holding {stash.Count} image(s) (max {ImageAttachmentStash.MaxPending}).");
            host.WakeUp();
            return Task.CompletedTask;
        }

        // With a path (typed or committed from the palette) attach straight
        // away; bare, open the palette's free-text input.
        string? path = ExtractPath(ctx.RawInput);
        if (path is not null)
        {
            return AttachAsync(host, stash, path, ct);
        }

        host.Palette.PushFrame(new PaletteFrame(
            "Attach image", "attach",
            [],
            IsInput: true,
            InputPlaceholder: "path to image...",
            OnInputSubmitAsync: (value, frameCt) => AttachAsync(host, stash, value, frameCt)));
        host.WakeUp();
        return Task.CompletedTask;
    }

    private async Task AttachAsync(IReplHost host, ImageAttachmentStash stash, string path, CancellationToken ct)
    {
        host.Palette.Hide();

        var read = await _reader
            .ReadAsync(path, host.SessionModel.ProviderId, host.SessionModel.Model, ct)
            .ConfigureAwait(false);

        if (read.IsFailure)
        {
            host.Bridge.AppendSystemLine($"! {read.Error}");
            host.WakeUp();
            return;
        }

        if (!stash.TryStage(read.Value))
        {
            host.Bridge.AppendSystemLine(
                $"! Cannot attach: limit of {ImageAttachmentStash.MaxPending} image(s) reached.");
            host.WakeUp();
            return;
        }

        host.Bridge.AppendSystemLine(
            $"📎 {Path.GetFileName(read.Value.Path)} · {read.Value.DimensionsLabel} · {read.Value.MimeType} — sent with your next message.");
        host.WakeUp();
    }

    /// <summary>
    ///     Path argument of a <c>/attach &lt;path&gt;</c> line, or null when the
    ///     command was invoked bare (palette commit). Paths with spaces are
    ///     re-joined from the remaining parts.
    /// </summary>
    private static string? ExtractPath(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput) || rawInput[0] != '/')
            return null;

        string[] parts = rawInput[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 2 ? null : string.Join(' ', parts, 1, parts.Length - 1).Trim();
    }
}
