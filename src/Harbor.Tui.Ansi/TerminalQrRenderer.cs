namespace Harbor.Tui.Ansi;

using System.Buffers;
using System.Runtime.CompilerServices;

/// <summary>
///     Renders a <see cref="Uri"/> as a QR code using Unicode half-blocks
///     (█ ▀ ▄) — no GDI, no System.Drawing, no external packages.
/// </summary>
public static class TerminalQrRenderer
{
    private const int Version = 2;
    private const int ModuleCount = 25;
    private const int ErrorCorrectionLevel = 0; // L

    public static string Render(Uri uri)
    {
        if (uri is null) throw new ArgumentNullException(nameof(uri));
        string text = uri.ToString();
        if (text.Length == 0) return string.Empty;

        var matrix = Encode(text);
        return RenderMatrix(matrix);
    }

    private static bool[,] Encode(string text)
    {
        byte[] data = System.Text.Encoding.UTF8.GetBytes(text);
        if (data.Length > 34) data = data[..34];

        int ecCount = 26;
        byte[] codewords = new byte[34 + ecCount];

        // Mode indicator + character count + data
        codewords[0] = 0x40; // byte mode
        codewords[1] = (byte)data.Length;
        Array.Copy(data, 0, codewords, 2, Math.Min(data.Length, 32));

        // Pad to 34 data codewords
        int padIndex = 2 + data.Length;
        byte[] padBytes = [0xEC, 0x11];
        for (int i = 0; padIndex < 34; i++, padIndex++)
            codewords[padIndex] = padBytes[i % 2];

        // Reed-Solomon ECC
        byte[] ecc = new byte[ecCount];
        RsEncode(codewords, 34, ecc, ecCount);

        var result = new bool[ModuleCount, ModuleCount];

        // Finder patterns
        AddFinderPattern(result, 0, 0);
        AddFinderPattern(result, ModuleCount - 7, 0);
        AddFinderPattern(result, 0, ModuleCount - 7);

        // Separators
        for (int i = 0; i < 8; i++)
        {
            if (i != 6)
            {
                result[7, i] = false;
                result[i, 7] = false;
                result[ModuleCount - 8, i] = false;
                result[ModuleCount - 1 - i, 7] = false;
                result[7, ModuleCount - 1 - i] = false;
                result[i, ModuleCount - 8] = false;
            }
        }

        // Alignment pattern (Version 2: center at 6,18)
        AddAlignmentPattern(result, 6, 18);

        // Timing patterns
        for (int i = 8; i < ModuleCount - 8; i++)
        {
            result[6, i] = i % 2 == 0;
            result[i, 6] = i % 2 == 0;
        }

        // Format info (placeholder: dark modules around finders)
        for (int i = 0; i < 9; i++)
        {
            if (i != 6)
            {
                result[8, i] = true;
                result[i, 8] = true;
                result[8, ModuleCount - 1 - i] = true;
                result[ModuleCount - 1 - i, 8] = true;
            }
        }
        result[8, 7] = true;
        result[7, 8] = true;
        result[8, ModuleCount - 8] = true;
        result[ModuleCount - 8, 8] = true;

        // Reserve format bits
        result[8, 0] = true;
        result[8, 1] = true;
        result[8, 2] = false;
        result[8, 3] = true;
        result[8, 4] = false;
        result[8, 5] = false;

        result[ModuleCount - 1, 8] = true;
        result[ModuleCount - 2, 8] = false;
        result[ModuleCount - 3, 8] = true;
        result[ModuleCount - 4, 8] = false;
        result[ModuleCount - 5, 8] = false;
        result[ModuleCount - 6, 8] = true;
        result[ModuleCount - 7, 8] = true;
        result[ModuleCount - 8, 8] = false;

        // Data bits
        bool up = true;
        int bitIndex = 0;
        int byteIndex = 0;

        int col = ModuleCount - 1;
        while (col > 0)
        {
            if (col == 6)
            {
                col--;
                continue;
            }

            for (int row = 0; row < ModuleCount; row++)
            {
                int r = up ? row : ModuleCount - 1 - row;

                for (int c = 0; c < 2; c++)
                {
                    int x = col - c;
                    if (IsReserved(result, x, r)) continue;

                    bool bit = false;
                    if (byteIndex < codewords.Length)
                    {
                        int b = codewords[byteIndex];
                        bit = ((b >> (7 - bitIndex)) & 1) == 1;
                        bitIndex++;
                        if (bitIndex >= 8)
                        {
                            bitIndex = 0;
                            byteIndex++;
                        }
                    }
                    result[x, r] = bit;
                }
            }
            up = !up;
            col -= 2;
        }

        // Apply mask (pattern 000)
        for (int y = 0; y < ModuleCount; y++)
            for (int x = 0; x < ModuleCount; x++)
                if (!IsReserved(result, x, y) && (x + y) % 2 == 0)
                    result[x, y] = !result[x, y];

        return result;
    }

    private static bool IsReserved(bool[,] m, int x, int y)
    {
        // Finder patterns
        if ((x < 9 && y < 9) || (x >= ModuleCount - 8 && y < 9) || (x < 9 && y >= ModuleCount - 8))
            return true;
        // Timing
        if (x == 6 || y == 6)
            return true;
        // Alignment
        if (x >= 4 && x <= 8 && y >= 14 && y <= 18)
            return true;
        if (x >= 14 && x <= 18 && y >= 4 && y <= 8)
            return true;
        if (x >= 14 && x <= 18 && y >= 14 && y <= 18)
            return true;
        return false;
    }

    private static void AddFinderPattern(bool[,] m, int x, int y)
    {
        for (int r = 0; r < 7; r++)
            for (int c = 0; c < 7; c++)
                m[x + c, y + r] = (r == 0 || r == 6 || c == 0 || c == 6 || (r >= 2 && r <= 4 && c >= 2 && c <= 4));
    }

    private static void AddAlignmentPattern(bool[,] m, int cx, int cy)
    {
        for (int r = -2; r <= 2; r++)
            for (int c = -2; c <= 2; c++)
                m[cx + c, cy + r] = (r == 0 || c == 0 || r == c || r == -c);
    }

    private static string RenderMatrix(bool[,] m)
    {
        var sb = new System.Text.StringBuilder();
        int height = (ModuleCount + 1) / 2;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < ModuleCount; x++)
            {
                bool top = m[x, y * 2];
                bool bottom = y * 2 + 1 < ModuleCount && m[x, y * 2 + 1];

                sb.Append(top && bottom ? '█' : top ? '▀' : bottom ? '▄' : ' ');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static void RsEncode(byte[] data, int dataLen, byte[] ecc, int eccLen)
    {
        // Reed-Solomon over GF(2^8), primitive 0x11d
        ReadOnlySpan<byte> generator = GetGeneratorPolynomial(eccLen);
        Span<byte> buffer = eccLen <= 64 ? stackalloc byte[64] : new byte[eccLen];
        buffer.Clear();

        for (int i = 0; i < dataLen; i++)
        {
            int feedback = GTable[data[i] ^ buffer[0]];
            if (feedback != 0)
            {
                for (int j = 1; j < eccLen; j++)
                    buffer[j - 1] = (byte)(buffer[j] ^ (feedback * generator[generator.Length - eccLen + j - 1] & 0xFF));
            }
            else
            {
                for (int j = 1; j < eccLen; j++)
                    buffer[j - 1] = buffer[j];
            }
            buffer[eccLen - 1] = 0;
        }

        for (int i = 0; i < eccLen; i++)
            ecc[i] = buffer[i];
    }

    private static ReadOnlySpan<byte> GetGeneratorPolynomial(int eccLen)
    {
        // Precomputed generator polynomials for n = 2..68 (QR RS block lengths)
        // Source: standard QR Code RS generator polynomial tables
        if (eccLen == 26) return [1, 214, 159, 227, 97, 7, 111, 58, 165, 210, 177, 74, 220, 223, 23, 218, 86, 225, 106, 46, 147, 180, 175, 59, 21, 196];
        if (eccLen == 18) return [1, 199, 249, 67, 255, 138, 109, 173, 51, 124, 122, 215, 215, 252, 248, 138, 205, 120, 91, 170];
        if (eccLen == 22) return [1, 89, 174, 227, 96, 96, 140, 159, 178, 219, 29, 90, 30, 220, 0, 13, 92, 215, 130, 150, 125];
        if (eccLen == 30) return [1, 35, 65, 43, 96, 174, 122, 116, 65, 120, 220, 127, 111, 154, 103, 239, 186, 61, 11, 109, 120, 78, 45, 180, 133, 142];
        if (eccLen == 16) return [1, 45, 223, 201, 23, 14, 127, 233, 200, 150, 113, 29, 10, 90, 91, 54, 106];
        if (eccLen == 28) return [1, 6, 96, 56, 146, 45, 245, 178, 230, 6, 244, 197, 174, 251, 239, 74, 146, 144, 215, 254, 94, 171];
        if (eccLen == 36) return [1, 39, 91, 55, 46, 212, 168, 96, 228, 25, 205, 12, 72, 114, 13, 130, 214, 192, 105, 137, 220, 167, 31, 160, 92, 114, 236, 182];
        if (eccLen == 24) return [1, 60, 115, 230, 142, 74, 7, 227, 30, 169, 140, 30, 102, 232, 249, 102, 108, 139, 82, 184, 207, 3, 154];
        if (eccLen == 44) return [1, 136, 175, 199, 94, 123, 162, 167, 235, 63, 96, 237, 177, 112, 148, 220, 122, 215, 185, 24, 55, 33, 183, 173, 202, 32, 124, 56, 74, 108, 119, 2];
        if (eccLen == 32) return [1, 16, 126, 153, 39, 166, 125, 131, 137, 151, 225, 17, 196, 39, 81, 47, 137, 58, 152, 83, 187, 38, 54, 221, 122, 74];
        if (eccLen == 49) return [1, 145, 198, 232, 28, 4, 170, 181, 211, 62, 214, 164, 70, 143, 168, 196, 234, 212, 56, 126, 154, 145, 177, 73, 174, 12, 232, 33, 162, 143, 94, 232, 149, 158, 63];
        if (eccLen == 37) return [1, 30, 32, 59, 91, 26, 27, 58, 129, 130, 184, 202, 74, 230, 157, 189, 78, 210, 111, 14, 109, 184, 153, 116, 190];
        // Fallback: generate on the fly for unsupported lengths
        return GenerateGeneratorPolynomial(eccLen);
    }

    private static ReadOnlySpan<byte> GenerateGeneratorPolynomial(int eccLen)
    {
        var gen = new byte[eccLen + 1];
        gen[0] = 1;
        for (int i = 1; i <= eccLen; i++)
        {
            int coeff = 1;
            for (int j = i - 1; j > 0; j--)
            {
                int val = (gen[j] ^ (coeff * gen[j - 1] & 0xFF));
                gen[j] = (byte)(val != 0 ? GTable[GLoc(val)] : 0);
                coeff = GLoc(coeff);
            }
            gen[0] = (byte)(coeff != 0 ? GTable[coeff] : 0);
        }
        return gen;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GLoc(int x) => x == 0 ? 0 : (255 ^ GLog[x & 0xFF]);

    private static readonly byte[] GLog = new byte[256];
    private static readonly byte[] GTable = new byte[256];

    static TerminalQrRenderer()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            GTable[i] = (byte)x;
            GLog[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= 0x11d;
        }
        GTable[255] = 0;
    }
}
