using System.Globalization;
using System.Text;

namespace Harbor.Tui.CellForge.CodeGen;

/// <summary>
///     Text helpers shared by the markup emitters. Keeping one copy makes the
///     generated C# identical across phases and keeps the emitters distinct.
/// </summary>
internal static class EmittedCSharp
{
    /// <summary>Quotes a value as a C# string literal.</summary>
    internal static string StringLiteral(string value)
    {
        var result = new StringBuilder(value.Length + 2);
        result.Append('"');
        foreach (var c in value)
        {
            Escape(result, c);
        }

        result.Append('"');
        return result.ToString();
    }

    private static void Escape(StringBuilder result, char c)
    {
        switch (c)
        {
            case '\\': result.Append("\\\\"); break;
            case '"': result.Append("\\\""); break;
            case '\n': result.Append("\\n"); break;
            case '\r': result.Append("\\r"); break;
            case '\t': result.Append("\\t"); break;
            default:
                if (char.IsControl(c))
                    result.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else
                    result.Append(c);
                break;
        }
    }
}
