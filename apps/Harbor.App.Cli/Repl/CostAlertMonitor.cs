using System.Globalization;
using Harbor.Abstractions.Events;
using Harbor.Tools.Builtin;

namespace Harbor.App.Cli.Repl;

/// <summary>
///     Session-scoped cost-alert threshold (issue #3109). Holds the value the
///     <c>/cost-alert</c> command parses and compares it with the running spend
///     carried by <see cref="SessionStatsEvent" />. Both slash entry points set
///     the same instance; the frame loop is the only observer.
/// </summary>
internal sealed class CostAlertMonitor
{
    private readonly object _gate = new();
    private CostAlertThreshold _threshold = CostAlertThreshold.Disabled;
    private bool _warned;

    internal CostAlertThreshold Threshold
    {
        get
        {
            lock (_gate)
            {
                return _threshold;
            }
        }
    }

    /// <summary>
    ///     Parses the slash argument with <see cref="CostAlertThreshold.Parse" />,
    ///     stores the result and returns the line the REPL writes back. An
    ///     invalid argument leaves the current threshold untouched.
    /// </summary>
    internal string Apply(string? argument)
    {
        CostAlertThreshold? parsed = CostAlertThreshold.Parse(argument);
        if (parsed is null)
        {
            return "Usage: /cost-alert <amount|off> — e.g. /cost-alert 1.50 or /cost-alert off.";
        }

        lock (_gate)
        {
            _threshold = parsed;
            _warned = false;
        }

        return parsed.IsEnabled
            ? $"✓ Cost alert set to {parsed}"
            : "✓ Cost alert disabled.";
    }

    /// <summary>
    ///     Compares a fresh spend snapshot with the stored threshold through
    ///     <see cref="CostAlertThreshold.IsExceeded" />. The warning fires once
    ///     per crossing; a snapshot back under the threshold re-arms it.
    /// </summary>
    internal string? Observe(decimal spentUsd)
    {
        CostAlertThreshold threshold;
        lock (_gate)
        {
            threshold = _threshold;
            if (!threshold.IsExceeded(spentUsd))
            {
                _warned = false;
                return null;
            }

            if (_warned)
            {
                return null;
            }

            _warned = true;
        }

        string spend = spentUsd.ToString("0.00", CultureInfo.InvariantCulture);
        return $"⚠ Cost alert: session spend ${spend} exceeded {threshold}.";
    }
}
