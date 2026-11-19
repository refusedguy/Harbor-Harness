using System;
using System.Collections.Generic;

namespace Harbor.Tui.CellForge.Generic.Forms;

/// <summary>One error row: the id that produced it and its message.</summary>
/// <param name="FieldId">Id of the invalid field.</param>
/// <param name="Message">Message to paint.</param>
public readonly record struct FieldError(string FieldId, string Message);

/// <summary>
///     Cell painter for any set of field errors. It knows neither the field
///     types nor where the messages came from; callers pass one row per error.
/// </summary>
public sealed class FieldErrorDisplay
{
    /// <summary>Prefix painted before each message; defaults to an exclamation mark.</summary>
    public string Prefix { get; set; } = "! ";

    /// <summary>Number of rows the supplied errors need.</summary>
    /// <param name="errors">Error rows.</param>
    /// <returns>One row per error.</returns>
    public int DesiredHeight(IReadOnlyList<FieldError> errors) => errors?.Count ?? 0;

    /// <summary>Paints one clipped message per row.</summary>
    /// <param name="buffer">Destination grid.</param>
    /// <param name="rect">Rectangle tall enough for the error rows.</param>
    /// <param name="errors">Error rows in paint order.</param>
    public void Paint(ScreenBuffer buffer, Rect rect, IReadOnlyList<FieldError> errors)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (rect.Width <= 0 || rect.Height <= 0 || errors is null)
        {
            return;
        }

        var style = new CellStyle(ChatPalette.Error);
        int count = Math.Min(errors.Count, rect.Height);
        for (int i = 0; i < count; i++)
        {
            string text = Prefix + errors[i].Message;
            if (text.Length > rect.Width)
            {
                text = text[..rect.Width];
            }

            buffer.SetText(rect.X, rect.Y + i, text, style);
        }
    }
}
