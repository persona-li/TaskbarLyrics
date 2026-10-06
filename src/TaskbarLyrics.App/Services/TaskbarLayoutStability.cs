using TaskbarLyrics.Windows;
namespace TaskbarLyrics.App.Services;

/// <summary>Confirms complete geometry, not just a chosen side. The clock is supplied by the caller.</summary>
public sealed class TaskbarLayoutStability
{
    public const int SettleMs = 140;
    public const int UnavailableSettleMs = 400;
    private Geometry? _candidate;
    private long _since;
    private int _samples;
    public bool Pending { get; private set; }
    public bool Observe(NativeMethods.RECT bar, TaskbarFreeRegionLocator.FreeRegionResult free, long now)
    {
        var next = new Geometry(bar.Left, bar.Top, bar.Right, bar.Bottom, free.Success,
            free.StartLeftPx, free.Left, free.Right);
        if (_candidate != next)
        {
            _candidate = next;
            _since = now;
            _samples = 1;
        }
        else _samples++;
        Pending = _samples < 3 || now - _since < (free.Success ? SettleMs : UnavailableSettleMs);
        return !Pending;
    }
    public void Reset() { _candidate = null; _samples = 0; Pending = true; }
    private sealed record Geometry(int Left, int Top, int Right, int Bottom, bool Measured,
        int Start, TaskbarInterval FreeLeft, TaskbarInterval FreeRight);
}
