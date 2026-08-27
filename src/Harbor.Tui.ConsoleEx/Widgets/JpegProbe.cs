using System.Buffers.Binary;

namespace Harbor.Tui.ConsoleEx.Widgets;

/// <summary>
/// Проверка JPEG-заголовка без декодера: сигнатура SOI + размеры из первого
/// SOF-маркера (baseline C0 / extended sequential C1 / progressive C2).
/// Хватает для превью-карточки («name · W×H»); полноценную растеризацию
/// позже отдают графику-протоколам терминала (sixel/kitty).
/// </summary>
public static class JpegProbe
{
    /// <summary>Размеры первого встреченного SOF-сегмента до SOS. Валидирует только маркерную структуру.</summary>
    public static bool TryReadDimensions(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = height = 0;
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return false;
        }

        int i = 2;
        while (i < data.Length)
        {
            if (data[i] != 0xFF)
            {
                return false; // потеряна маркерная синхронизация потока
            }

            // Забивные 0xFF перед кодом маркера легальны — пропускаем их.
            int j = i + 1;
            while (j < data.Length && data[j] == 0xFF)
            {
                j++;
            }

            if (j >= data.Length || data[j] == 0x00)
            {
                return false;
            }

            byte marker = data[j];
            int segStart = j + 1;
            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                return ReadSof(data, segStart, ref width, ref height);
            }

            // RSTn / SOI / TEM идут без length-поля; EOI/SOS означают конец метаданных.
            if (marker == 0x01 || marker is >= 0xD0 and <= 0xD7 || marker == 0xD8)
            {
                i = segStart;
                continue;
            }

            if (marker == 0xD9 || marker == 0xDA || segStart + 2 > data.Length)
            {
                return false;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(data[segStart..]);
            if (length < 2)
            {
                return false;
            }

            i = segStart + length;
        }

        return false;
    }

    private static bool ReadSof(ReadOnlySpan<byte> data, int segStart, ref int width, ref int height)
    {
        // Контент сегмента: [precision(1)][height(2) BE][width(2) BE][...]
        if (segStart + 7 > data.Length)
        {
            return false;
        }

        uint h = BinaryPrimitives.ReadUInt16BigEndian(data[(segStart + 3)..]);
        uint w = BinaryPrimitives.ReadUInt16BigEndian(data[(segStart + 5)..]);
        if (w == 0 || h == 0)
        {
            return false; // нулевые размеры считаем битым файлом
        }

        width = (int)w;
        height = (int)h;
        return true;
    }
}
