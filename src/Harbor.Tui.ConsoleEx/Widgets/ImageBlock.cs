using System.Buffers.Binary;
using System.Text;
using Harbor.Tui.ConsoleEx.Rendering;

namespace Harbor.Tui.ConsoleEx.Widgets;

/// <summary>
/// Проверка PNG-заголовка без декодера: сигнатура + размеры из IHDR.
/// Хватает для превью-карточки («name · W×H»); полноценную растеризацию
/// позже отдают графику-протоколам терминала (sixel/kitty).
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
/// Карточка изображения в таймлайне: имя, формат по MIME, «W×H» при живом
/// PNG-заголовке и объём. Рисуется текстовой картой — инлайн-графику
/// (sixel/kitty/iTerm2) добавит будущий бэкенд с passthrough-эскейпами.
/// </summary>
public sealed class ImageBlock : IChatBlock
{
    private const int LeftPad = 2;

    public ImageBlock(string path, string mimeType, long sizeBytes, byte[]? data)
    {
        Name = Path.GetFileName(string.IsNullOrWhiteSpace(path) ? "?" : path);
        MimeType = string.IsNullOrWhiteSpace(mimeType) ? "?" : mimeType;
        SizeBytes = Math.Max(0, sizeBytes);

        IsImage = MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        int w = 0, h = 0;
        HasPngHeader = IsImage && data is { Length: >= 24 } && PngProbe.TryReadDimensions(data, out w, out h);
        if (!HasPngHeader)
        {
            HasJpegHeader = IsImage && data is { Length: > 0 } && JpegProbe.TryReadDimensions(data, out w, out h);
        }

        Dimensions = w > 0 ? $"{w}×{h}" : null;
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

    /// <summary>Строка «W×H», когда заголовок распознан.</summary>
    public string? Dimensions { get; }

    public string Kind => "image";

    public bool IsStreamContinuation => false;

    public int BudgetBytes => 96 + (Name.Length * 2) + MimeType.Length;

    public BlockMeasure Measure(int width) => BlockMeasure.Exact(2);

    public int CheapEstimate(int width) => 2;

    public void Paint(in BlockPaintContext ctx)
    {
        var buffer = ctx.Buffer;
        if (ctx.Rect.Width <= 0 || ctx.Rect.Height < 2)
        {
            return;
        }

        buffer.SetText(ctx.Rect.X + LeftPad, ctx.Rect.Y,
            Truncate(IsImage ? $"◉ {Name}" : $"≣ {Name}", ctx.Rect.Width - LeftPad),
            IsImage ? ChatPalette.ToolOk : ChatPalette.ToolArgs);

        buffer.SetText(ctx.Rect.X + LeftPad, ctx.Rect.Y + 1,
            Truncate(SummaryLine(), ctx.Rect.Width - LeftPad), ChatPalette.Dim);
    }

    internal string SummaryLine()
    {
        string dims = Dimensions ?? MimeType;
        return $"{dims} · {FormatSize(SizeBytes)}";
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

    private static string Truncate(string s, int max) =>
        max <= 0 ? string.Empty : s.Length <= max ? s : s[..max];
}
