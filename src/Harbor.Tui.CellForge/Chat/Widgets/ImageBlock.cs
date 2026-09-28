using System.Buffers.Binary;
using System.Text;
using Harbor.Tui.CellForge.Capabilities;
using Harbor.Ui.Framework.Rendering;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// Проверка PNG-заголовка без декодера: сигнатура + размеры из IHDR.
/// Хватает для превью-карточки («name · W×H»); полноценную растеризацию
/// отдают графику-протоколам терминала (kitty APC / OSC 1337).
/// </summary>
public static class PngProbe
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Размеры первого IHDR-чанка. Валидирует только первые 24 байта.</summary>
    public static bool TryReadDimensions(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        if (data.Length < 24 || !data[..8].SequenceEqual(Signature))
        {
            return false;
        }

        // Байты 12..16 — тип чанка "IHDR"; 16..20 и 20..24 — ширина/высота big-endian.
        if (!data[12..16].SequenceEqual("IHDR"u8))
        {
            return false;
        }

        uint w = BinaryPrimitives.ReadUInt32BigEndian(data[16..20]);
        uint h = BinaryPrimitives.ReadUInt32BigEndian(data[20..24]);
        if (w is 0 or > uint.MaxValue / 2 || h is 0)
        {
            return false; // нулевые/нелепые размеры считаем битым файлом
        }

        width = w <= int.MaxValue ? (int)w : int.MaxValue;
        height = h <= int.MaxValue ? (int)h : int.MaxValue;
        return true;
    }
}

/// <summary>
/// Карточка изображения в таймлайне.
/// <para>
/// Два режима, выбираемые <em>только</em> пробой протокола
/// (<see cref="InlineImageProbe" />), а не веткой в хосте:
/// <list type="bullet">
///   <item><description><b>Графика</b> — терминал говорит kitty APC или
///   OSC 1337: блок отдаёт закодированный payload в
///   <see cref="IInlineImageSink" />, вписывая его в свой cell-rect, и рисует
///   поверх только однострочную подпись. Размер считается из
///   <em>пробинговых пиксельных</em> размеров и прямоугольника блока
///   (шириной блока, соотношением сторон картинки и клипом по высоте).</description></item>
///   <item><description><b>Текст</b> — всё остальное (пайпы, CI, tmux/screen,
///   простой xterm, битые байты): прежняя двухстрочная карточка, побайтово
///   та же, что до #387.</description></item>
/// </list>
/// </para>
/// <para>
/// Закодированный payload кэшируется на блок и живёт ровно столько же,
/// сколько сам блок в таймлайне: <b>ни один байт картинки не удерживается
/// на кадр</b>. Пересчёт идёт только когда сменились протокол или целевой
/// размер в ячейках.
/// </para>
/// </summary>
public sealed class ImageBlock : IChatBlock
{
    /// <summary>
    /// Left gutter the card indents by. Internal (not private) so the
    /// fullscreen viewer fits the image to the same inner width the feed used —
    /// one constant, so 100% zoom in the viewer is exactly what the timeline
    /// drew.
    /// </summary>
    internal const int LeftPad = 2;

    /// <summary>Минимальная высота графической области — ниже карточка нечитаема.</summary>
    public const int MinGraphicRows = 2;

    /// <summary>
    /// Максимальная высота графической области. Ограничивает и layout, и
    /// полезную площадь: картинка на 40 строк в ленте выдавливает всё
    /// остальное, а полноразмерный просмотр — работа вьювера.
    /// </summary>
    public const int MaxGraphicRows = 24;

    /// <summary>
    /// Соотношение сторон ячейки терминала (высота к ширине). Стандартный
    /// шрифт — примерно 1:2, поэтому квадратная картинка занимает вдвое
    /// больше строк, чем колонок. Задано константой, а не зондом: детектить
    /// пиксель терминала дороже, чем точность, которую даёт константа.
    /// </summary>
    public const double CellAspect = 2.0;

    // ENG10 #282: transition-computed paint lines (all inputs immutable) —
    // Paint only slices spans over these, no per-frame interpolation.
    private readonly string _line1;
    private readonly string _summary;

    // #387: the encoded escape payload, cached per block. Null until the first
    // graphics paint, and re-encoded only when the target cell box changes — a
    // plain scroll keeps the same rect and reuses the buffer. The -1 sentinel
    // on the geometry keys means "never encoded", so a failed encode is
    // memoized as well and costs nothing per frame.
    private byte[]? _payload;
    private int _payloadCols = -1;
    private int _payloadRows = -1;

    /// <summary>
    /// The session's inline-image capability, sampled ONCE at construction.
    /// <para><b>Why a constructor argument and not a paint-time probe.</b>
    /// <see cref="Measure" /> is the layout's only input and must stay pure —
    /// it is cached per width by the timeline. If the block reserved graphic
    /// height unconditionally, a text-only session (pipes, CI, tmux/screen)
    /// would reserve ~15 rows per screenshot and paint a 2-line card, leaving a
    /// hole in the feed. Sampling the sink here makes the reserved height and
    /// the painted content agree by construction, and keeps
    /// <see cref="Measure" /> a pure function of immutable state.
    /// </para>
    /// <para>The host sets the timeline's sink once at startup, before any
    /// block is appended, so the sample is never stale. The paint path still
    /// re-checks the live sink, so a late-wired host degrades to the card
    /// rather than to a hole.</para>
    /// </summary>
    public ImageBlock(string path, string mimeType, long sizeBytes, byte[]? data, bool graphicsAvailable = false)
    {
        Name = Path.GetFileName(string.IsNullOrWhiteSpace(path) ? "?" : path);
        MimeType = string.IsNullOrWhiteSpace(mimeType) ? "?" : mimeType;
        SizeBytes = Math.Max(0, sizeBytes);
        GraphicsAvailable = graphicsAvailable;

        IsImage = MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        int w = 0, h = 0;
        HasPngHeader = IsImage && data is { Length: >= 24 } && PngProbe.TryReadDimensions(data, out w, out h);
        if (!HasPngHeader)
        {
            HasJpegHeader = IsImage && data is { Length: > 0 } && JpegProbe.TryReadDimensions(data, out w, out h);
        }

        // The bytes themselves, kept for the block's lifetime: the encoded
        // payload is derived from them lazily on the first graphics paint and
        // cached beside it, so nothing is re-derived per frame. Non-image
        // attachments (a PDF next to a screenshot) are probed-and-dropped
        // exactly as before.
        if (IsImage && data is { Length: > 0 })
        {
            Data = data;
        }

        PixelWidth = w > 0 ? w : 0;
        PixelHeight = h > 0 ? h : 0;
        Dimensions = w > 0 ? $"{w}×{h}" : null;

        // Corrupt / truncated PNG/JPEG: the mime names a format we DO probe,
        // yet no header parsed. Kept as a flag rather than an exception — the
        // card still paints, with a dim warning marker, so a bad attachment
        // degrades to a card instead of taking the frame down. Formats we do
        // not probe (webp, gif, …) are never "damaged": not reading their
        // dimensions is not evidence of corruption.
        IsDamaged = IsImage && Data is not null && PixelWidth == 0 && IsProbedFormat;

        _line1 = (IsImage ? "◉ " : "≣ ") + Name;
        _summary = (Dimensions ?? MimeType) + " · " + FormatSize(SizeBytes);
    }

    /// <summary>Имя файла без директорий.</summary>
    public string Name { get; }

    public string MimeType { get; }

    public long SizeBytes { get; }

    /// <summary>MIME начинается с image/.</summary>
    public bool IsImage { get; }

    /// <summary>Байт-данные прошли проверку PNG IHDR.</summary>
    public bool HasPngHeader { get; }

    /// <summary>Байт-данные прошли проверку JPEG SOI+SOF (PNG-проба молчит).</summary>
    public bool HasJpegHeader { get; }

    /// <summary>
    /// Probed pixel width (0 when no header parsed). The graphics path scales
    /// from these, never from the byte count.
    /// </summary>
    public int PixelWidth { get; }

    /// <summary>Probed pixel height (0 when no header parsed).</summary>
    public int PixelHeight { get; }

    /// <summary>
    /// MIME says image/ but no header parsed — corrupt or truncated bytes.
    /// The block degrades to the text card with a dim ⚠ marker.
    /// </summary>
    public bool IsDamaged { get; }

    /// <summary>Строка «W×H», когда заголовок распознан.</summary>
    public string? Dimensions { get; }

    /// <summary>
    /// True when the session sampled as graphics-capable at construction —
    /// the switch that decides whether this block reserves graphic height
    /// (<see cref="Measure" />) or stays a two-line card. See the ctor.
    /// </summary>
    public bool GraphicsAvailable { get; }

    public string Kind => "image";

    public bool IsStreamContinuation => false;

    /// <summary>
    /// True for the two formats whose headers we can actually read (PNG IHDR,
    /// JPEG SOF). Only these can be reported as damaged — see the ctor.
    /// </summary>
    private bool IsProbedFormat =>
        MimeType.EndsWith("png", StringComparison.OrdinalIgnoreCase)
        || MimeType.EndsWith("jpeg", StringComparison.OrdinalIgnoreCase)
        || MimeType.EndsWith("jpg", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resident size for <see cref="TimelineRing" /> eviction. #387: the raw
    /// bytes AND the base64-encoded payload are resident, so both count —
    /// otherwise a multi-megabyte screenshot would sit in a 1 MiB ring without
    /// ever being counted, and the ring would stop evicting anything.
    /// </summary>
    public int BudgetBytes
    {
        get
        {
            long total = 96L + (Name.Length * 2L) + MimeType.Length
                + (Data?.Length ?? 0)
                + (_payload?.Length ?? 0);
            return (int)Math.Min(total, int.MaxValue);
        }
    }

    public BlockMeasure Measure(int width)
    {
        int rows = GraphicHeight(width);
        return rows > 0 ? BlockMeasure.Exact(rows + 1) : BlockMeasure.Exact(2);
    }

    public int CheapEstimate(int width) => Measure(width).MinLines;

    public void Paint(in BlockPaintContext ctx)
    {
        var buffer = ctx.Buffer;
        if (ctx.Rect.Width <= 0 || ctx.Rect.Height < 2)
        {
            return;
        }

        int avail = ctx.Rect.Width - LeftPad;
        if (avail <= 0)
        {
            return;
        }

        // Graphics path: the block hands its cached payload to the sink and
        // keeps only the caption row for itself. The image occupies the rows
        // ABOVE the caption, so the timeline's own text never fights the
        // bitmap for the same cells. `graphicRows < ctx.Rect.Height` leaves at
        // least one row for the caption — a fully-clipped block stays on the
        // text card rather than dropping its own label.
        int graphicRows = GraphicHeight(ctx.Rect.Width);
        if (graphicRows > 0 && graphicRows < ctx.Rect.Height && TryPaintGraphic(ctx, graphicRows))
        {
            PaintSummary(buffer, ctx.Rect.X + LeftPad, ctx.Rect.Bottom - 1, avail);
            return;
        }

        // Text fallback — byte-identical to the pre-#387 card.
        var line1 = _line1.AsSpan();
        buffer.SetText(ctx.Rect.X + LeftPad, ctx.Rect.Y,
            line1.Slice(0, Math.Min(avail, line1.Length)),
            IsImage ? ChatPalette.ToolOk : ChatPalette.ToolArgs);

        PaintSummary(buffer, ctx.Rect.X + LeftPad, ctx.Rect.Y + 1, avail);

        // Damage marker sits at the far right of the caption row: a dim ⚠
        // that does not disturb the pinned card text.
        if (IsDamaged && ctx.Rect.Width > LeftPad + 1)
        {
            buffer.SetRune(ctx.Rect.Right - 1, ctx.Rect.Y + 1, new Rune('⚠'), ChatPalette.Dim);
        }
    }

    /// <summary>
    /// Height in rows the image itself occupies at <paramref name="width" />,
    /// or 0 when this block has nothing to draw graphically: a text-only
    /// session (see <see cref="GraphicsAvailable" />), a non-image mime, no
    /// bytes, or a header that did not parse.
    /// </summary>
    public int GraphicHeight(int width) =>
        GraphicsAvailable ? FittedGraphicHeight(width) : 0;

    /// <summary>
    /// The aspect-fitted graphic height for <paramref name="width" />,
    /// independent of whether this session can draw it. The fullscreen viewer
    /// uses this (it has its own box, not the feed's), while
    /// <see cref="GraphicHeight" /> gates the feed reservation.
    /// </summary>
    internal int FittedGraphicHeight(int width)
    {
        if (PixelWidth <= 0 || PixelHeight <= 0 || width <= LeftPad)
        {
            return 0;
        }

        int cols = width - LeftPad;
        // rows = cols * (ph/pw) / cellAspect — a 100×200 image at 40 columns
        // is 20 rows, not 80: the cell aspect eats the height.
        double rows = cols * ((double)PixelHeight / PixelWidth) / CellAspect;
        if (rows < MinGraphicRows)
        {
            rows = MinGraphicRows;
        }

        return rows > MaxGraphicRows ? MaxGraphicRows : (int)Math.Round(rows);
    }

    /// <summary>
    /// True when this block can place a graphic at all: it holds bytes, its
    /// header parsed, and the session's sink speaks a protocol. Deliberately
    /// takes the sink, not the protocol — the block never learns whether the
    /// terminal is kitty or iTerm2.
    /// </summary>
    public bool CanRenderGraphic(IInlineImageSink? sink) =>
        sink is { Enabled: true } && PixelWidth > 0 && PixelHeight > 0 && Data is not null;

    /// <summary>
    /// The encoded escape payload for a <paramref name="cols"/>×<paramref name="rows"/>
    /// cell box, or null when the session cannot carry this image. Cached per
    /// (cols, rows) on the block, so a plain scroll re-encodes nothing and the
    /// array is released with the block — no image byte array is retained per
    /// frame anywhere in the pipeline.
    /// </summary>
    internal byte[]? EncodedPayload(IInlineImageSink? sink, int cols, int rows)
    {
        if (!CanRenderGraphic(sink))
        {
            return null;
        }

        // Memo keyed on the cell box. A miss is memoized too (-1 never matches
        // a real geometry), so a picture the protocol cannot carry costs ONE
        // failed encode per size instead of one per frame.
        if (_payloadCols == cols && _payloadRows == rows)
        {
            return _payload is { Length: > 0 } ? _payload : null;
        }

        sink!.TryEncode(Name, MimeType, Data, cols, rows, out _payload);
        _payloadCols = cols;
        _payloadRows = rows;
        return _payload is { Length: > 0 } ? _payload : null;
    }

    internal string SummaryLine() => _summary;

    /// <summary>Raw image bytes, or null when the block holds no payload.</summary>
    private byte[]? Data { get; }

    /// <summary>
    /// Places the image and blanks its own cells so the diff sees a stable
    /// background. Returns false when the sink is off or encoding failed, in
    /// which case the caller paints the text card instead.
    /// </summary>
    private bool TryPaintGraphic(in BlockPaintContext ctx, int rows)
    {
        var sink = ctx.InlineImages;
        if (sink is not { Enabled: true })
        {
            return false;
        }

        // Clip BEFORE encoding: the payload names the box the terminal must
        // fill, so a rect trimmed after the fact would ask for rows the frame
        // no longer has. The timeline's own rect is already inside the frame,
        // so this is normally a no-op — it matters when a host hands us a
        // partially-scrolled block.
        Rect target = sink.ClipToFrame(new Rect(ctx.Rect.X + LeftPad, ctx.Rect.Y, ctx.Rect.Width - LeftPad, rows));
        if (target.Width <= 0 || target.Height <= 0)
        {
            return false;
        }

        byte[]? payload = EncodedPayload(sink, target.Width, target.Height);
        if (payload is null)
        {
            return false;
        }

        // Blank the graphic area: the cell diff must not leave a stale text
        // card visible behind a bitmap that the terminal draws over it.
        ctx.Buffer.Fill(new Rect(ctx.Rect.X, ctx.Rect.Y, ctx.Rect.Width, rows), Cell.Blank);
        sink.Place(target, payload);
        return true;
    }

    private void PaintSummary(ScreenBuffer buffer, int x, int y, int avail)
    {
        var line2 = _summary.AsSpan();
        buffer.SetText(x, y, line2.Slice(0, Math.Min(avail, line2.Length)), ChatPalette.Dim);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} MB",
        >= 1024 => $"{bytes / 1024d:0.#} KB",
        _ => $"{bytes} B",
    };

    public string RawText() =>
        new StringBuilder(Name.Length + MimeType.Length + 32)
            .Append(Name).Append(' ').AppendLine(MimeType).Append(SummaryLine()).ToString();
}
