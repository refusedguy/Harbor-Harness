using System;
using Harbor.Ui.Framework.Converters;

namespace Harbor.Ui.Framework.Animation;

/// <summary>
///     Animates the running cost label while an agent session is active.
///     The platform VM owns the UI-thread timer and calls <see cref="Advance"/>
///     on each interval.
/// </summary>
public sealed class CostAnimator : IDisposable
{
    private decimal _baseCost;
    private DateTime? _startTime;
    private bool _disposed;

    public decimal BaseCost
    {
        get => _baseCost;
        set
        {
            _baseCost = value;
            if (_startTime is null)
                DisplayCost = value;
        }
    }

    public decimal DisplayCost { get; private set; }
    public bool IsRunning { get; private set; }
    public string AnimatedText => StatusMappers.CostToUsd(DisplayCost);

    public event Action? Tick;

    public void Start(decimal baseCost)
    {
        _baseCost = baseCost;
        DisplayCost = baseCost;
        _startTime = DateTime.UtcNow;
        IsRunning = true;
    }

    public void Stop()
    {
        _startTime = null;
        IsRunning = false;
    }

    public void Advance()
    {
        if (_startTime is not { } start)
        {
            Stop();
            return;
        }

        var elapsed = DateTime.UtcNow - start;
        DisplayCost = _baseCost + (decimal)(elapsed.TotalSeconds * 0.0001);
        Tick?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Tick = null;
        _disposed = true;
    }
}
