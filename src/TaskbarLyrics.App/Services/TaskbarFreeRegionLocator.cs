using System.Windows.Automation;
using TaskbarLyrics.Windows;
namespace TaskbarLyrics.App.Services;

public static class TaskbarFreeRegionLocator
{
    public sealed record FreeRegionResult(bool Success, int StartLeftPx, TaskbarInterval Left, TaskbarInterval Right, string Source)
    {
        public int AvailableWidthPx => Left.Width;
    }
    public static FreeRegionResult Measure(NativeMethods.RECT taskbar, int leftMarginPx, int rightSafetyPx)
    {
        FreeRegionResult Fail(string reason) => new(false, 0, default, default, reason);
        try
        {
            var tray = NativeMethods.FindWindow(TaskbarLocator.PrimaryTaskbarClassName, null);
            if (tray == IntPtr.Zero) return Fail("Taskbar unavailable");
            var root = AutomationElement.FromHandle(tray);
            var request = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.None };
            request.Add(AutomationElement.BoundingRectangleProperty);
            request.Add(AutomationElement.IsOffscreenProperty);
            request.Add(AutomationElement.AutomationIdProperty);
            request.Add(AutomationElement.ClassNameProperty);
            request.Add(AutomationElement.NameProperty);
            using var cache = request.Activate();
            var controls = root.FindAll(TreeScope.Descendants, new OrCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem)));
            var occupied = new List<TaskbarInterval>();
            int? start = null, trayLeft = null;
            var nativeTray = NativeMethods.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            if (nativeTray != IntPtr.Zero && NativeMethods.GetWindowRect(nativeTray, out var trayRect) && trayRect.Width > 0)
                trayLeft = Math.Clamp(trayRect.Left, taskbar.Left, taskbar.Right);
            foreach (AutomationElement element in controls)
            {
                // A disappearing element means an inconsistent snapshot; retry next positioning tick.
                var current = element.Cached;
                var rect = current.BoundingRectangle;
                if (current.IsOffscreen || rect.IsEmpty || !double.IsFinite(rect.Left) || rect.Width <= 0 || rect.Height <= 0
                    || rect.Bottom <= taskbar.Top || rect.Top >= taskbar.Bottom || rect.Right <= taskbar.Left || rect.Left >= taskbar.Right) continue;
                var bounds = new TaskbarInterval(Math.Max(taskbar.Left, (int)Math.Floor(rect.Left)), Math.Min(taskbar.Right, (int)Math.Ceiling(rect.Right)));
                var id = current.AutomationId ?? "";
                var cls = current.ClassName ?? "";
                var name = current.Name ?? "";
                if (id.Equals("StartButton", StringComparison.OrdinalIgnoreCase) || id.Equals("Start", StringComparison.OrdinalIgnoreCase)
                    || new[] { "Start", "开始", "開始", "スタート", "시작" }.Contains(name)) start = bounds.Left;
                if (cls.StartsWith("SystemTray.", StringComparison.Ordinal) || id == "SystemTrayIcon" || id == "NotifyItemIcon")
                    trayLeft = Math.Min(trayLeft ?? taskbar.Right, bounds.Left);
                occupied.Add(bounds);
            }
            if (start is null || trayLeft is null) return Fail("Start or system tray boundary unavailable");
            if (trayLeft < start) return Fail("Inconsistent taskbar bounds");
            var left = TaskbarRegionLayout.LargestGap(taskbar.Left, start.Value, occupied, leftMarginPx, rightSafetyPx);
            // All controls between Start and the tray belong to the application/control cluster.
            // Its rightmost edge (not the gap between individual icons) bounds the right band.
            var clusterRight = occupied.Where(x => x.Left >= start && x.Left < trayLeft).Select(x => x.Right).DefaultIfEmpty(start.Value).Max();
            var right = TaskbarRegionLayout.LargestGap(Math.Min(clusterRight, trayLeft.Value), trayLeft.Value,
                occupied, leftMarginPx, rightSafetyPx);
            return new(true, start.Value, left, right, "UIA controls + system tray boundary");
        }
        catch (Exception ex) { return Fail("Measurement failed: " + ex.GetType().Name); }
    }
}
