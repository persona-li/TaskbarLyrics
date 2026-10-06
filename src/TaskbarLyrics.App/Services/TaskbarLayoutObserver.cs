using System.Windows.Automation;
using TaskbarLyrics.Windows;
namespace TaskbarLyrics.App.Services;

// UIA registration runs off the WPF thread; the callback only requests a coalesced layout pass.
public sealed class TaskbarLayoutObserver : IDisposable
{
    private readonly object _gate = new();
    private readonly Action _changed;
    private AutomationElement? _root;
    private IntPtr _handle;
    private volatile bool _disposed;
    private int _pending;
    private long _retryAt;
    private readonly AutomationPropertyChangedEventHandler _bounds;
    private readonly StructureChangedEventHandler _structure;
    public TaskbarLayoutObserver(Action changed)
    {
        _changed = changed;
        _bounds = (_, _) => Notify(); _structure = (_, _) => Notify();
    }
    private void Notify() { if (!_disposed) _changed(); }
    public void EnsureAttached()
    {
        var handle = NativeMethods.FindWindow(TaskbarLocator.PrimaryTaskbarClassName, null);
        if (!Monitor.TryEnter(_gate)) return;
        try { if (_disposed || handle == IntPtr.Zero || handle == _handle || Environment.TickCount64 < _retryAt) return; }
        finally { Monitor.Exit(_gate); }
        if (Interlocked.Exchange(ref _pending, 1) != 0) return;
        _ = Task.Run(() =>
        {
            lock (_gate)
            {
                try
                {
                    if (_disposed) return;
                    Detach();
                    _root = AutomationElement.FromHandle(handle);
                    Automation.AddAutomationPropertyChangedEventHandler(_root, TreeScope.Subtree, _bounds, AutomationElement.BoundingRectangleProperty);
                    Automation.AddStructureChangedEventHandler(_root, TreeScope.Subtree, _structure);
                    _handle = handle;
                }
                catch { Detach(); _retryAt = Environment.TickCount64 + 5000; }
                finally { Interlocked.Exchange(ref _pending, 0); }
            }
        });
    }
    private void Detach()
    {
        if (_root is not null)
        {
            try { Automation.RemoveAutomationPropertyChangedEventHandler(_root, _bounds); } catch { }
            try { Automation.RemoveStructureChangedEventHandler(_root, _structure); } catch { }
        }
        _root = null; _handle = IntPtr.Zero;
    }
    public void Dispose()
    {
        _disposed = true;
        _ = Task.Run(() => { lock (_gate) Detach(); });
    }
}
