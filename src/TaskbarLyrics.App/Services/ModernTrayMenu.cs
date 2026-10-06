using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using TaskbarLyrics.App.Config;
using TaskbarLyrics.App.Presentation;
using DrawingColor = System.Drawing.Color;
using Forms = System.Windows.Forms;
using WpfApp = System.Windows.Application;

namespace TaskbarLyrics.App.Services;

/// <summary>
/// Flat compact tray menu.
///
/// Why outside-click can leave a "stuck" menu:
/// We open via delayed <see cref="ToolStripDropDown.Show(Point)"/> (so overflow can collapse).
/// That path does NOT always run the shell modal menu loop, so clicks on other windows
/// never reach us and AutoClose never fires. Fix: WH_MOUSE_LL while the menu is open.
/// </summary>
public sealed class ModernTrayMenu : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly RoundedContextMenuStrip _menu;
    private readonly ConfigService _config;
    private readonly Action _openSettings;
    private readonly Action _rematch;
    private readonly Action _exit;
    private readonly Action<bool> _setOverlayVisible;

    private readonly Forms.ToolStripMenuItem _toggleItem;
    private readonly Forms.ToolStripMenuItem _exitItem;
    private readonly Forms.ToolStripMenuItem _rematchItem;
    private readonly Forms.ToolStripLabel _trackLabel, _stateLabel;
    private readonly Func<OverlayUiState>? _readState;
    private readonly FlatTrayRenderer _renderer;
    private readonly Font _font;
    private System.Threading.Timer? _showTimer;
    private DateTime _openedUtc = DateTime.MinValue;
    private bool _disposed;

    // Outside-click dismiss (must keep delegate alive).
    private IntPtr _mouseHook;
    private LowLevelMouseProc? _mouseProc;

    public ModernTrayMenu(
        Forms.NotifyIcon icon,
        ConfigService config,
        Action openSettings,
        Action rematch,
        Action exit,
        Action<bool> setOverlayVisible, Func<OverlayUiState>? readState = null)
    {
        _icon = icon;
        _config = config;
        _openSettings = openSettings;
        _rematch = rematch;
        _exit = exit;
        _setOverlayVisible = setOverlayVisible;
        _readState = readState;

        // Keep the compact tray surface consistent with the management window.
        _renderer = new FlatTrayRenderer
        {
            MenuCornerRadius = 8,
            ItemCornerRadius = 8,
        };
        _font = new Font("Microsoft YaHei UI", 9.25f, FontStyle.Regular, GraphicsUnit.Point);

        _menu = new RoundedContextMenuStrip
        {
            Renderer = _renderer,
            Font = _font,
            ShowImageMargin = false,
            ShowCheckMargin = true,
            AutoSize = true,
            AutoClose = true,
            DropShadowEnabled = true,
            Padding = new Forms.Padding(6, 6, 6, 6),
            Margin = new Forms.Padding(0),
            ImageScalingSize = new Size(14, 14),
            CornerRadius = 8,
        };

        ApplyThemeColors();

        _trackLabel = new CenteredTrayLabel("TaskbarLyrics") { AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Forms.Padding(8, 4, 8, 4) };
        _stateLabel = new CenteredTrayLabel("等待播放") { AutoSize = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Forms.Padding(8, 4, 8, 4) };
        _menu.Items.Add(_trackLabel); _menu.Items.Add(_stateLabel); _menu.Items.Add(MakeSeparator());
        _toggleItem = MakeItem("显示任务栏歌词", OnToggle);
        _menu.Items.Add(_toggleItem);
        _menu.Items.Add(MakeSeparator());
        _menu.Items.Add(MakeItem("打开窗口", () => { CloseMenu(); Dispatch(_openSettings); }));
        _rematchItem = MakeItem("选择歌词", () => { CloseMenu(); Dispatch(_rematch); });
        _menu.Items.Add(_rematchItem);
        _menu.Items.Add(MakeSeparator());
        _exitItem = MakeItem("退出", () => { CloseMenu(); Dispatch(_exit); });
        _exitItem.ForeColor = _renderer.Colors.Text;
        _menu.Items.Add(_exitItem);

        // Fully own show/position (no shell ContextMenuStrip assignment).
        _icon.ContextMenuStrip = null;
        _icon.MouseUp += OnIconMouseUp;
        _menu.Opening += (_, _) => RefreshToggleState();
        _menu.Closed += (_, _) => UninstallOutsideClickHook();
        _menu.VisibleChanged += (_, _) =>
        {
            if (!_menu.Visible)
            {
                UninstallOutsideClickHook();
            }
        };
        ApplyThemeColors();
        ThemeManager.ThemeChanged += OnThemeChanged;

        RefreshToggleState();
    }

    private static Forms.ToolStripSeparator MakeSeparator() =>
        new()
        {
            Margin = new Forms.Padding(10, 4, 10, 4),
            Padding = new Forms.Padding(0),
        };

    private Forms.ToolStripMenuItem MakeItem(string text, Action onClick)
    {
        // Vertical padding raises row height ~10% vs the previous 0/0 vertical pad.
        var item = new CenteredTrayMenuItem(text)
        {
            AutoSize = true,
            Padding = new Forms.Padding(8, 5, 8, 5),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Forms.Padding(1, 1, 1, 1),
            Font = _font,
        };
        item.Click += (_, _) => onClick();
        return item;
    }

    private void OnIconMouseUp(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button is not (Forms.MouseButtons.Left or Forms.MouseButtons.Right))
        {
            return;
        }

        App.Logger?.Info($"Tray {e.Button}-click → schedule menu");

        // Toggle if already open.
        if (_menu.Visible)
        {
            CloseMenu();
            return;
        }

        // Cancel any pending open, then wait for overflow flyout to collapse.
        try { _showTimer?.Dispose(); } catch { /* ignore */ }
        _showTimer = new System.Threading.Timer(
            _ => Dispatch(ShowAnchoredMenu),
            null,
            dueTime: 120,
            period: Timeout.Infinite);
    }

    private void ShowAnchoredMenu()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_menu.Visible)
            {
                return;
            }

            RefreshToggleState();
            ApplyThemeColors();
            _exitItem.ForeColor = _renderer.Colors.Text;

            var anchor = TrayAnchor.GetMenuAnchor(_icon);
            App.Logger?.Info($"Tray menu anchor=({anchor.X},{anchor.Y})");

            _menu.Show(new System.Drawing.Point(anchor.X, anchor.Y), Forms.ToolStripDropDownDirection.AboveLeft);
            _openedUtc = DateTime.UtcNow;

            // Delayed-Show is not a shell modal menu → install LL mouse hook for outside dismiss.
            // Do NOT Focus() the drop-down: that can promote it to a taskbar "ghost app" button.
            InstallOutsideClickHook();
        }
        catch (Exception ex)
        {
            App.Logger?.Error("Tray menu show failed", ex);
            try
            {
                _menu.Show(Forms.Cursor.Position);
                _openedUtc = DateTime.UtcNow;
                InstallOutsideClickHook();
            }
            catch
            {
                // ignore
            }
        }
    }

    private void CloseMenu()
    {
        try { _showTimer?.Dispose(); } catch { /* ignore */ }
        _showTimer = null;
        UninstallOutsideClickHook();
        try
        {
            if (_menu.Visible)
            {
                _menu.Close();
            }
        }
        catch
        {
            // ignore
        }
    }

    private void InstallOutsideClickHook()
    {
        UninstallOutsideClickHook();
        try
        {
            _mouseProc = OutsideMouseHook;
            using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule!;
            _mouseHook = SetWindowsHookEx(
                WH_MOUSE_LL,
                _mouseProc,
                GetModuleHandle(curModule.ModuleName),
                0);

            if (_mouseHook == IntPtr.Zero)
            {
                App.Logger?.Warn("Tray outside-click mouse hook failed");
            }
        }
        catch (Exception ex)
        {
            App.Logger?.Error("InstallOutsideClickHook", ex);
        }
    }

    private void UninstallOutsideClickHook()
    {
        try
        {
            if (_mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
        }
        catch
        {
            // ignore
        }

        _mouseProc = null;
    }

    private IntPtr OutsideMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && _menu.Visible)
            {
                var msg = wParam.ToInt32();
                if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_NCLBUTTONDOWN or WM_NCRBUTTONDOWN)
                {
                    // Ignore the open-gesture window so we don't instantly close.
                    if ((DateTime.UtcNow - _openedUtc).TotalMilliseconds < 200)
                    {
                        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
                    }

                    var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    var pt = new System.Drawing.Point(info.pt.x, info.pt.y);

                    // Screen bounds of the drop-down (include shadow margin).
                    var bounds = _menu.Bounds;
                    bounds.Inflate(4, 4);

                    if (!bounds.Contains(pt))
                    {
                        // Close on UI thread; do not swallow the click.
                        Dispatch(CloseMenu);
                    }
                }
            }
        }
        catch
        {
            // never break the hook chain
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void OnToggle()
    {
        var next = !_config.Current.General.ShowOverlay;
        _config.Update(c => c.General.ShowOverlay = next);
        Dispatch(() => _setOverlayVisible(next));
        RefreshToggleState();
        // Keep menu open after toggle (normal context-menu check item behavior).
    }

    public void RefreshPresentation() => RefreshToggleState();

    private void RefreshToggleState()
    {
        if (_disposed) return;
        if (_readState is not null)
        {
            var s = PlaybackPresentation.From(_readState());
            _trackLabel.Text = s.HasTrack ? (s.Title.Length > 18 ? s.Title[..18] + "…" : s.Title) : "TaskbarLyrics";
            _trackLabel.ToolTipText = s.HasTrack ? s.Raw.Track!.DisplayName : "任务栏歌词助手";
            _stateLabel.Text = s.HasTrack ? s.PlaybackLabel + " · " + s.StatusTitle : s.StatusTitle;
            _rematchItem.Enabled = s.HasTrack && !s.Raw.LyricsLoading;
        }
        _toggleItem.Checked = _config.Current.General.ShowOverlay;
    }

    private void OnThemeChanged()
    {
        try
        {
            ApplyThemeColors();
            _exitItem.ForeColor = _renderer.Colors.Text;
            _menu.Invalidate();
        }
        catch
        {
            // ignore
        }
    }

    private void ApplyThemeColors()
    {
        static DrawingColor ReadColor(string key)
        {
            if (WpfApp.Current?.TryFindResource(key) is System.Windows.Media.SolidColorBrush b)
                return DrawingColor.FromArgb(b.Color.A, b.Color.R, b.Color.G, b.Color.B);
            return key.Contains("Background") ? SystemColors.Window : SystemColors.WindowText;
        }
        _renderer.Colors = new TrayMenuColors
        {
            Background = ReadColor("SurfaceBrush"), Border = ReadColor("BorderBrush"),
            Text = ReadColor("TextPrimaryBrush"), Hover = ReadColor("PlaybackHoverBrush"),
            Separator = ReadColor("BorderSubtleBrush"), Check = ReadColor("AccentBrush"), DangerText = ReadColor("DangerBrush")
        };
        if (_trackLabel is not null) _trackLabel.ForeColor = ReadColor("TextPrimaryBrush");
        if (_stateLabel is not null) _stateLabel.ForeColor = ReadColor("TextSecondaryBrush");
        if (_menu is not null)
        {
            _menu.BackColor = _renderer.Colors.Background;
            _menu.ForeColor = _renderer.Colors.Text;
        }
    }

    private static void Dispatch(Action action)
    {
        var app = WpfApp.Current;
        if (app?.Dispatcher is null)
        {
            return;
        }

        if (app.Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        app.Dispatcher.BeginInvoke(action, DispatcherPriority.Input);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ThemeManager.ThemeChanged -= OnThemeChanged;
        try { _showTimer?.Dispose(); } catch { /* ignore */ }
        UninstallOutsideClickHook();
        try { _icon.MouseUp -= OnIconMouseUp; } catch { /* ignore */ }
        try { _icon.ContextMenuStrip = null; } catch { /* ignore */ }
        try { _menu.Dispose(); } catch { /* ignore */ }
        try { _font.Dispose(); } catch { /* ignore */ }
    }

    #region Low-level mouse hook

    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCRBUTTONDOWN = 0x00A4;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    #endregion
}

/// <summary>Full-row tray heading whose preferred size does not depend on its arranged width.</summary>
internal sealed class CenteredTrayLabel(string text) : Forms.ToolStripLabel(text)
{
    public override Size GetPreferredSize(Size constrainingSize)
    {
        var textSize = Forms.TextRenderer.MeasureText(Text, Font, Size.Empty,
            Forms.TextFormatFlags.SingleLine | Forms.TextFormatFlags.NoPrefix | Forms.TextFormatFlags.NoPadding);
        return new Size(textSize.Width + Padding.Horizontal, textSize.Height + Padding.Vertical);
    }

    public void StretchRow(int left, int width)
    {
        if (Bounds.X != left || Bounds.Width != width)
            SetBounds(new Rectangle(left, Bounds.Y, width, Bounds.Height));
    }
}

internal sealed class CenteredTrayMenuItem(string text) : Forms.ToolStripMenuItem(text)
{
    public void StretchRow(int left, int width)
    {
        if (Bounds.X != left || Bounds.Width != width)
            SetBounds(new Rectangle(left, Bounds.Y, width, Bounds.Height));
    }
}

/// <summary>
/// ContextMenuStrip with rounded region + no taskbar button.
/// Without WS_EX_TOOLWINDOW, showing the drop-down can spawn a useless default-icon
/// taskbar entry (common WPF + WinForms tray-menu glitch).
/// </summary>
internal sealed class RoundedContextMenuStrip : Forms.ContextMenuStrip
{
    private bool _aligningRows;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;
    private const int WsExNoActivate = 0x08000000;
    private const int GwlExStyle = -20;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public int CornerRadius { get; set; } = 14;

    public override Size GetPreferredSize(Size proposedSize)
    {
        var preferred = base.GetPreferredSize(proposedSize);
        if (Items.Count == 0) return preferred;
        // Match the renderer's symmetric check gutters. Derive width only from
        // text, never the stretched row bounds, so repeated layouts cannot grow it.
        var gutter = (int)Math.Round(22 * DeviceDpi / 96d);
        var textWidth = Items.Cast<Forms.ToolStripItem>().Where(item => item is not Forms.ToolStripSeparator)
            .Select(item => Forms.TextRenderer.MeasureText(item.Text, item.Font, Size.Empty,
                Forms.TextFormatFlags.SingleLine | Forms.TextFormatFlags.NoPrefix | Forms.TextFormatFlags.NoPadding).Width)
            .DefaultIfEmpty(0).Max();
        return new Size(textWidth + gutter * 2 + 12, preferred.Height);
    }

    protected override void OnLayout(Forms.LayoutEventArgs e)
    {
        if (_aligningRows) return;
        _aligningRows = true;
        try
        {
            foreach (var label in Items.OfType<CenteredTrayLabel>())
                label.StretchRow(label.Bounds.X, label.GetPreferredSize(Size.Empty).Width);
            base.OnLayout(e);
            // Native menu layout reserves an asymmetric check column and can report
            // item bounds wider than the client area. Keep the whole rounded hover
            // surface inside the menu while preserving its check slot and row height.
            foreach (var row in Items.OfType<CenteredTrayMenuItem>())
                row.StretchRow(0, Math.Max(1, ClientSize.Width));
            var action = Items.OfType<Forms.ToolStripMenuItem>().FirstOrDefault();
            if (action is null) return;
            foreach (var label in Items.OfType<CenteredTrayLabel>())
                label.StretchRow(action.Bounds.X, action.Bounds.Width);
        }
        finally { _aligningRows = false; }
    }

    // The real tray menu is WinForms, so WPF's keyboard policy cannot cover it.
    // Consume menu keys before ToolStrip forwards them to the selected item.
    protected override bool ProcessCmdKey(ref Forms.Message message, Forms.Keys keyData) =>
        !IsWindowManagementKey(keyData) || base.ProcessCmdKey(ref message, keyData);

    protected override bool ProcessDialogKey(Forms.Keys keyData) =>
        !IsWindowManagementKey(keyData) || base.ProcessDialogKey(keyData);

    protected override bool ProcessDialogChar(char charCode) => true;
    protected override bool ProcessMnemonic(char charCode) => true;

    private static bool IsWindowManagementKey(Forms.Keys keyData)
    {
        var key = keyData & Forms.Keys.KeyCode;
        return key is Forms.Keys.LWin or Forms.Keys.RWin
            || key == Forms.Keys.F4 && (keyData & Forms.Keys.Alt) != 0;
    }

    protected override Forms.CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            // Never appear as a normal taskbar application button.
            cp.ExStyle |= WsExToolWindow | WsExNoActivate;
            cp.ExStyle &= ~WsExAppWindow;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Belt-and-suspenders: some .NET versions re-apply APPWINDOW after handle creation.
        try
        {
            var ex = GetWindowLong(Handle, GwlExStyle);
            ex |= WsExToolWindow | WsExNoActivate;
            ex &= ~WsExAppWindow;
            SetWindowLong(Handle, GwlExStyle, ex);
        }
        catch
        {
            // ignore
        }

        ApplyRoundRegion();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static int GetWindowLong(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex).ToInt32() : GetWindowLong32(hWnd, nIndex);

    private static void SetWindowLong(IntPtr hWnd, int nIndex, int value)
    {
        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr64(hWnd, nIndex, new IntPtr(value));
        }
        else
        {
            SetWindowLong32(hWnd, nIndex, value);
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyRoundRegion();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            BeginInvoke(ApplyRoundRegion);
        }
    }

    private void ApplyRoundRegion()
    {
        if (!IsHandleCreated || Width <= 0 || Height <= 0)
        {
            return;
        }

        try
        {
            var r = Math.Max(4, CornerRadius);
            var hRgn = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, r * 2, r * 2);
            if (hRgn != IntPtr.Zero)
            {
                Region?.Dispose();
                Region = Region.FromHrgn(hRgn);
                DeleteObject(hRgn);
            }
        }
        catch
        {
            // ignore
        }
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}

internal sealed class TrayMenuColors
{
    public DrawingColor Background { get; set; }
    public DrawingColor Border { get; set; }
    public DrawingColor Text { get; set; }
    public DrawingColor Hover { get; set; }
    public DrawingColor Separator { get; set; }
    public DrawingColor Check { get; set; }
    public DrawingColor DangerText { get; set; }
}

internal sealed class FlatTrayRenderer : Forms.ToolStripProfessionalRenderer
{
    public TrayMenuColors Colors { get; set; } = new();
    public int MenuCornerRadius { get; set; } = 14;
    public int ItemCornerRadius { get; set; } = 8;

    public FlatTrayRenderer() : base(new FlatTrayColorTable())
    {
        RoundedEdges = true;
    }

    protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Colors.Background);
        using var brush = new SolidBrush(Colors.Background);
        var rect = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        using var path = RoundRect(rect, MenuCornerRadius);
        g.FillPath(brush, path);
    }

    protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var rect = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        using var pen = new Pen(Colors.Border, 1.2f);
        using var path = RoundRect(rect, MenuCornerRadius);
        g.DrawPath(pen, path);
    }

    protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected && !e.Item.Pressed)
        {
            return;
        }

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        // Slight inset so hover pills sit inside the outer rounded panel.
        var rect = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
        using var brush = new SolidBrush(Colors.Hover);
        using var path = RoundRect(rect, ItemCornerRadius);
        g.FillPath(brush, path);
    }

    protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.ForeColor.IsEmpty || e.Item.ForeColor.ToArgb() == SystemColors.ControlText.ToArgb()
            ? Colors.Text
            : e.Item.ForeColor;
        // Keep every label on the same left text edge, clear of the checkmark slot.
        var gutter = (int)Math.Round(22 * (e.ToolStrip?.DeviceDpi ?? 96) / 96d);
        e.TextRectangle = new Rectangle(gutter, 0, Math.Max(1, e.Item.Width - gutter * 2), e.Item.Height);
        e.TextFormat = Forms.TextFormatFlags.Left | Forms.TextFormatFlags.VerticalCenter
            | Forms.TextFormatFlags.SingleLine | Forms.TextFormatFlags.EndEllipsis
            | Forms.TextFormatFlags.NoPrefix | Forms.TextFormatFlags.NoPadding;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
    {
        var g = e.Graphics;
        var y = e.Item.ContentRectangle.Top + e.Item.ContentRectangle.Height / 2;
        using var pen = new Pen(Colors.Separator);
        g.DrawLine(pen, 10, y, e.Item.Width - 10, y);
    }

    protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var cx = e.ImageRectangle.Left + e.ImageRectangle.Width / 2;
        var cy = e.ImageRectangle.Top + e.ImageRectangle.Height / 2;
        using var pen = new Pen(Colors.Check, 1.6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        g.DrawLines(pen, new[]
        {
            new System.Drawing.Point(cx - 4, cy),
            new System.Drawing.Point(cx - 1, cy + 3),
            new System.Drawing.Point(cx + 5, cy - 4),
        });
    }

    protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e)
    {
    }

    private static GraphicsPath RoundRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        if (d <= 0)
        {
            path.AddRectangle(rect);
            return path;
        }

        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class FlatTrayColorTable : Forms.ProfessionalColorTable
{
    public override DrawingColor MenuBorder => DrawingColor.Transparent;
    public override DrawingColor MenuItemBorder => DrawingColor.Transparent;
    public override DrawingColor MenuItemSelected => DrawingColor.Transparent;
    public override DrawingColor MenuItemSelectedGradientBegin => DrawingColor.Transparent;
    public override DrawingColor MenuItemSelectedGradientEnd => DrawingColor.Transparent;
    public override DrawingColor ImageMarginGradientBegin => DrawingColor.Transparent;
    public override DrawingColor ImageMarginGradientMiddle => DrawingColor.Transparent;
    public override DrawingColor ImageMarginGradientEnd => DrawingColor.Transparent;
    public override DrawingColor SeparatorDark => DrawingColor.Transparent;
    public override DrawingColor SeparatorLight => DrawingColor.Transparent;
    public override DrawingColor ToolStripDropDownBackground => DrawingColor.Transparent;
    public override DrawingColor CheckBackground => DrawingColor.Transparent;
    public override DrawingColor CheckSelectedBackground => DrawingColor.Transparent;
    public override DrawingColor CheckPressedBackground => DrawingColor.Transparent;
}
