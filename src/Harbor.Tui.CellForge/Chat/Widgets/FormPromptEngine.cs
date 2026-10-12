using System.Globalization;

namespace Harbor.Tui.CellForge.Widgets;

/// <summary>
/// What <see cref="FormPromptState{T}.ReadAndSubmit"/> decided for one line
/// from an <see cref="IFormInputSource"/> (the Spectre.Console
/// <c>TextPrompt.Show</c> loop minus the console: the host owns keys and
/// repaint, the engine owns text in / value-or-error out).
/// </summary>
public enum FormPromptOutcome
{
    /// <summary>Line converted and every chained validator passed — read the value.</summary>
    Submitted = 0,

    /// <summary>Source returned null (EOF / dismiss) — the host hides without committing.</summary>
    Cancelled = 1,

    /// <summary>Conversion or a validator failed — read the error and re-prompt.</summary>
    Invalid = 2,
}

/// <summary>
/// One conversion attempt (the Spectre.Console <c>TypeConverter</c> +
/// <c>Culture</c> half of <c>TextPrompt{T}</c>, as data: no exceptions, no
/// reflection — AOT-clean by construction).
/// </summary>
/// <typeparam name="T">Prompt value type.</typeparam>
public readonly record struct FormConvertResult<T>(bool Ok, T Value, string Error);

/// <summary>
/// String ↔ value converter for <see cref="FormPromptState{T}"/> (the
/// Spectre.Console <c>TypeConverter</c> seam, hand-rolled: only
/// <see cref="FormConverters"/> plus <see cref="FromDelegate"/> exist, so no
/// reflection-driven conversion can sneak onto the AOT path).
/// </summary>
/// <typeparam name="T">Prompt value type.</typeparam>
public sealed class FormValueConverter<T> where T : notnull
{
    private readonly Func<string, CultureInfo, FormConvertResult<T>> _tryConvert;
    private readonly Func<T, CultureInfo, string> _toDisplay;

    public FormValueConverter(
        Func<string, CultureInfo, FormConvertResult<T>> tryConvert,
        Func<T, CultureInfo, string>? toDisplay = null)
    {
        ArgumentNullException.ThrowIfNull(tryConvert);
        _tryConvert = tryConvert;
        if (toDisplay is null)
        {
            _toDisplay = static (v, _) => v.ToString() ?? string.Empty;
        }
        else
        {
            _toDisplay = toDisplay;
        }
    }

    /// <summary>Converts raw text; failure carries the message the prompt shows.</summary>
    public FormConvertResult<T> TryConvert(string text, CultureInfo culture) => _tryConvert(text, culture);

    /// <summary>Formats a value back to editable text (defaults, echo).</summary>
    public string ToDisplay(T value, CultureInfo culture) => _toDisplay(value, culture);

    /// <summary>Custom converter (the <c>TypeConverterAttribute</c> escape hatch, as a delegate).</summary>
    public static FormValueConverter<T> FromDelegate(
        Func<string, CultureInfo, FormConvertResult<T>> tryConvert,
        Func<T, CultureInfo, string>? toDisplay = null) => new(tryConvert, toDisplay);
}

/// <summary>
/// Built-in converters (the four <c>TextPrompt{T}</c> instantiations worth
/// porting: string/int/double/bool — everything else arrives via
/// <see cref="FormValueConverter{T}.FromDelegate"/>).
/// </summary>
public static class FormConverters
{
    /// <summary>Identity (empty text stays empty; validators do the work).</summary>
    public static FormValueConverter<string> String() =>
        FormValueConverter<string>.FromDelegate(static (text, _) =>
            new FormConvertResult<string>(true, text, string.Empty));

    /// <summary>32-bit integer, parsed under the prompt culture.</summary>
    public static FormValueConverter<int> Int32() =>
        FormValueConverter<int>.FromDelegate(static (text, culture) =>
            int.TryParse(text, NumberStyles.Integer, culture, out int v)
                ? new FormConvertResult<int>(true, v, string.Empty)
                : new FormConvertResult<int>(false, 0, $"'{text}' is not a valid integer."));

    /// <summary>Double, parsed under the prompt culture (the comma-decimal case).</summary>
    public static FormValueConverter<double> Double() =>
        FormValueConverter<double>.FromDelegate(static (text, culture) =>
            double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, culture, out double v)
                ? new FormConvertResult<double>(true, v, string.Empty)
                : new FormConvertResult<double>(false, 0, $"'{text}' is not a valid number."));

    /// <summary>y/yes/true/1 vs n/no/false/0 (the <c>ConfirmationPrompt</c> vocabulary, case-insensitive).</summary>
    public static FormValueConverter<bool> Boolean() =>
        FormValueConverter<bool>.FromDelegate(static (text, _) =>
        {
            string t = text.Trim();
            if (t.Equals("y", StringComparison.OrdinalIgnoreCase)
                || t.Equals("yes", StringComparison.OrdinalIgnoreCase)
                || t.Equals("true", StringComparison.OrdinalIgnoreCase)
                || t.Equals("1", StringComparison.Ordinal))
            {
                return new FormConvertResult<bool>(true, true, string.Empty);
            }
            if (t.Equals("n", StringComparison.OrdinalIgnoreCase)
                || t.Equals("no", StringComparison.OrdinalIgnoreCase)
                || t.Equals("false", StringComparison.OrdinalIgnoreCase)
                || t.Equals("0", StringComparison.Ordinal))
            {
                return new FormConvertResult<bool>(true, false, string.Empty);
            }
            return new FormConvertResult<bool>(false, false, $"Please answer y or n (got '{text}').");
        });
}

/// <summary>
/// Injectable line source (the Spectre.Console console-read seam: the engine
/// never touches <c>Console</c>, so tests and golden fixtures drive it with
/// <see cref="QueueFormInputSource"/> — the #1228 VirtualInputSource direction).
/// </summary>
public interface IFormInputSource
{
    /// <summary>Next line, or null on EOF/dismiss.</summary>
    string? ReadLine();
}

/// <summary>In-memory <see cref="IFormInputSource"/> (tests, fixtures, scripted demos).</summary>
public sealed class QueueFormInputSource : IFormInputSource
{
    private readonly Queue<string?> _lines = new();

    /// <summary>Enqueues one line (null = EOF/dismiss at that position).</summary>
    public void Enqueue(string? line) => _lines.Enqueue(line);

    /// <inheritdoc />
    public string? ReadLine() => _lines.Count == 0 ? null : _lines.Dequeue();
}

/// <summary>
/// Generic prompt state ([compose/spectre] SC6, part of #1214, epic #1195):
/// port of Spectre.Console <c>TextPrompt{T}</c> minus the console/render half
/// (painting stays in the owning widget, as with SC7's list engine).
/// <list type="bullet">
/// <item><description><c>Validate()</c> calls CHAIN (each appends; nothing overwrites) — the wrapping semantics.</description></item>
/// <item><description>Conversion is a <see cref="FormValueConverter{T}"/> under <see cref="Culture"/>.</description></item>
/// <item><description><see cref="DefaultValue"/> (+ <see cref="HasDefault"/>), <see cref="AllowEmpty"/>, secret <see cref="MaskedText"/>, <see cref="Choices"/>.</description></item>
/// </list>
/// Engine-independent: raw text in, value-or-error out. No keys — the host owns
/// the key gate (so no KeyGateFamily row), no rendering, no nullable public
/// surface (so no CellForgeWidgetState row).
/// </summary>
/// <typeparam name="T">Prompt value type.</typeparam>
public sealed class FormPromptState<T> where T : notnull
{
    private readonly FormValueConverter<T> _converter;
    private readonly List<Func<T, string?>> _validators = [];
    private readonly List<Func<string, string?>> _textValidators = [];
    private readonly List<T> _choices = [];
    private CultureInfo _culture;
    private T _defaultValue;
    private string _text = string.Empty;
    private string _error = string.Empty;

    public FormPromptState(FormValueConverter<T> converter, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(converter);
        _converter = converter;
        _culture = culture ?? CultureInfo.InvariantCulture;
        _defaultValue = default!;
    }

    /// <summary>Raw input text (empty = no input yet).</summary>
    public string Text => _text;

    /// <summary>Last validation error (empty = valid / not validated yet).</summary>
    public string Error => _error;

    /// <summary>Culture for conversion and default formatting.</summary>
    public CultureInfo Culture
    {
        get => _culture;
        set => _culture = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Empty input is accepted (without a default, only string prompts submit it).</summary>
    public bool AllowEmpty { get; set; }

    /// <summary>When true, <see cref="MaskedText"/> hides <see cref="Text"/> (the Secret/Mask axis).</summary>
    public bool IsSecret { get; set; }

    /// <summary>Mask rune for <see cref="MaskedText"/> (default '*').</summary>
    public char MaskChar { get; set; } = '*';

    /// <summary>True after <see cref="SetDefault"/> (empty input submits the default).</summary>
    public bool HasDefault { get; private set; }

    /// <summary>Default value (meaningful only when <see cref="HasDefault"/>).</summary>
    public T DefaultValue => _defaultValue;

    /// <summary>Allowed values in host order (empty = unconstrained — the Choices axis).</summary>
    public IReadOnlyList<T> Choices => _choices;

    /// <summary>Echo text: masked when <see cref="IsSecret"/>, raw otherwise.</summary>
    public string MaskedText() => IsSecret ? new string(MaskChar, _text.Length) : _text;

    /// <summary>Replaces the raw text (null coerces to empty) and clears <see cref="Error"/>.</summary>
    public void SetText(string? text)
    {
        _text = text ?? string.Empty;
        _error = string.Empty;
    }

    /// <summary>Sets the default submitted for empty input (the DefaultValue/EditableDefaultValue axis).</summary>
    public void SetDefault(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _defaultValue = value;
        HasDefault = true;
    }

    /// <summary>Clears the default (empty input goes through the AllowEmpty path again).</summary>
    public void ClearDefault()
    {
        _defaultValue = default!;
        HasDefault = false;
    }

    /// <summary>Replaces the choice set (copied; empty = unconstrained).</summary>
    public void SetChoices(IReadOnlyList<T> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        _choices.Clear();
        for (int i = 0; i < choices.Count; i++)
        {
            if (choices[i] is null)
            {
                throw new ArgumentException("Choice must not be null.", nameof(choices));
            }
            _choices.Add(choices[i]);
        }
    }

    /// <summary>
    /// Appends a typed validator (Spectre.Console <c>Validate()</c> wrapping
    /// semantics: every call ADDS — a later call never removes an earlier one).
    /// Return null/empty to pass, an error message to fail (first failure wins).
    /// </summary>
    public void AddValidator(Func<T, string?> validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        _validators.Add(validator);
    }

    /// <summary>Appends a raw-text validator (runs before conversion).</summary>
    public void AddTextValidator(Func<string, string?> validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        _textValidators.Add(validator);
    }

    /// <summary>Resets the text to the formatted default (or empty) and clears <see cref="Error"/>.</summary>
    public void ResetToDefault()
    {
        _text = HasDefault ? _converter.ToDisplay(_defaultValue, _culture) : string.Empty;
        _error = string.Empty;
    }

    /// <summary>Validates the current <see cref="Text"/>; sets <see cref="Error"/> and returns it (empty = ok).</summary>
    public string Validate()
    {
        _error = ValidateCore(out _);
        return _error;
    }

    /// <summary>Validates and converts; true + value on success, false + error (also in <see cref="Error"/>) on failure.</summary>
    public bool TrySubmit(out T value, out string error)
    {
        error = ValidateCore(out value);
        _error = error;
        return error.Length == 0;
    }

    /// <summary>Reads one line (null = cancelled) and submits it — the scripted-prompt path.</summary>
    public FormPromptOutcome ReadAndSubmit(IFormInputSource source, out T value, out string error)
    {
        ArgumentNullException.ThrowIfNull(source);
        string? line = source.ReadLine();
        if (line is null)
        {
            value = default!;
            error = string.Empty;
            return FormPromptOutcome.Cancelled;
        }
        SetText(line);
        return TrySubmit(out value, out error) ? FormPromptOutcome.Submitted : FormPromptOutcome.Invalid;
    }

    private string ValidateCore(out T value)
    {
        value = default!;
        for (int i = 0; i < _textValidators.Count; i++)
        {
            string? failure = _textValidators[i](_text);
            if (!string.IsNullOrEmpty(failure))
            {
                return failure;
            }
        }
        if (_text.Length == 0)
        {
            if (HasDefault)
            {
                return ValidateValue(_defaultValue, out value);
            }
            if (AllowEmpty && typeof(T) == typeof(string))
            {
                value = (T)(object)string.Empty;
                return string.Empty;
            }
            return "Value is required.";
        }
        FormConvertResult<T> converted = _converter.TryConvert(_text, _culture);
        if (!converted.Ok)
        {
            return converted.Error;
        }
        return ValidateValue(converted.Value, out value);
    }

    private string ValidateValue(T candidate, out T value)
    {
        value = candidate;
        if (_choices.Count > 0 && !IsListed(candidate))
        {
            return "Value is not one of the available choices.";
        }
        for (int i = 0; i < _validators.Count; i++)
        {
            string? failure = _validators[i](candidate);
            if (!string.IsNullOrEmpty(failure))
            {
                return failure;
            }
        }
        return string.Empty;
    }

    private bool IsListed(T candidate)
    {
        var comparer = EqualityComparer<T>.Default;
        for (int i = 0; i < _choices.Count; i++)
        {
            if (comparer.Equals(_choices[i], candidate))
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// Yes/no prompt state (the Spectre.Console <c>ConfirmationPrompt</c> half of
/// SC6: y/yes vs n/no, trimmed, case-insensitive, empty = default).
/// </summary>
public sealed class FormConfirmState
{
    private string _text = string.Empty;
    private string _error = string.Empty;

    /// <summary>Raw input text.</summary>
    public string Text => _text;

    /// <summary>Last validation error (empty = valid / not validated yet).</summary>
    public string Error => _error;

    /// <summary>Submitted for empty input (the [y/n] default).</summary>
    public bool DefaultChoice { get; set; } = true;

    /// <summary>Replaces the raw text (null coerces to empty) and clears <see cref="Error"/>.</summary>
    public void SetText(string? text)
    {
        _text = text ?? string.Empty;
        _error = string.Empty;
    }

    /// <summary>Validates the current <see cref="Text"/>; sets <see cref="Error"/> and returns it (empty = ok).</summary>
    public string Validate()
    {
        _error = ValidateCore(out _);
        return _error;
    }

    /// <summary>Validates; true + choice on success, false + error on failure.</summary>
    public bool TrySubmit(out bool choice, out string error)
    {
        error = ValidateCore(out choice);
        _error = error;
        return error.Length == 0;
    }

    private string ValidateCore(out bool choice)
    {
        string t = _text.Trim();
        if (t.Length == 0)
        {
            choice = DefaultChoice;
            return string.Empty;
        }
        if (t.Equals("y", StringComparison.OrdinalIgnoreCase) || t.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            choice = true;
            return string.Empty;
        }
        if (t.Equals("n", StringComparison.OrdinalIgnoreCase) || t.Equals("no", StringComparison.OrdinalIgnoreCase))
        {
            choice = false;
            return string.Empty;
        }
        choice = false;
        return $"Please answer y or n (got '{_text}').";
    }
}
