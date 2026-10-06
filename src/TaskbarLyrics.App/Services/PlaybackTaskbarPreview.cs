using System.Globalization;
using Microsoft.Win32;
using System.Runtime.CompilerServices;
using TaskbarLyrics.Core.Models;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using System.Windows.Threading;
using TaskbarLyrics.App.Presentation;
using TaskbarLyrics.Windows;

namespace TaskbarLyrics.App.Services;

public sealed class PlaybackTaskbarPreview : IDisposable
{
    private static readonly ConditionalWeakTable<LyricsDocument, IReadOnlyList<LyricReaderRow>> TranslationRows = new();
    public static string? CurrentTranslation(PlaybackPresentation state)
    {
        if (!state.HasTrack || state.Health != LyricsHealth.Ready || state.Raw.Document is not { } document
            || state.Raw.Lyric.LineIndex < 0) return null;
        return TranslationRows.GetValue(document, LyricReaderRows.Build)
            .FirstOrDefault(row => row.Index == state.Raw.Lyric.LineIndex)?.Translation;
    }
    private readonly Window _window;
    private readonly SettingsViewModel _model;
    private readonly HwndSource _source;
    private readonly DispatcherTimer _timer;
    private readonly ThumbButtonInfo _previous, _play, _next;
    private string? _lastKey, _iconKey, _transportColorKey, _lastSize;
    private long _lastRefresh;
    private ResourceDictionary _shellColors = new();
    private bool? _shellLight;
    private bool _disposed;
    public PlaybackTaskbarPreview(Window window, SettingsViewModel model)
    {
        _window=window; _model=model;
        RefreshShellColors();
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        _source=HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!;
        _previous=new() {Description="上一首", Command=new TaskbarPreviewCommand(model.PreviousTrackCommand), DismissWhenClicked=false};
        _play=new() {Description="播放", Command=new TaskbarPreviewCommand(model.TogglePlaybackCommand), DismissWhenClicked=false};
        _next=new() {Description="下一首", Command=new TaskbarPreviewCommand(model.NextTrackCommand), DismissWhenClicked=false};
        window.TaskbarItemInfo=new TaskbarItemInfo { Description="TaskbarLyrics", ThumbButtonInfos=new ThumbButtonInfoCollection {_previous,_play,_next} };
        _source.AddHook(Hook);
        TaskbarThumbnail.Enable(_source.Handle);
        _timer=new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_,_)=>Refresh(), window.Dispatcher);
        Refresh();
    }
    private void Refresh()
    {
        if (!_window.IsVisible) return;
        var now=Environment.TickCount64;
        if (now-_lastRefresh>=500) { _model.Refresh(); _lastRefresh=now; }
        var state=_model.State;
        // Preserve native button visuals during requests; execution still uses the live guard.
        if (!_model.IsPlaybackControlBusy)
        {
            _previous.IsEnabled=_model.CanSkipPrevious;
            _next.IsEnabled=_model.CanSkipNext;
            _play.IsEnabled=_model.CanTogglePlayback;
        }
        _play.Description=_model.PlaybackButtonLabel;
        var color=Brush("PlaybackIndicatorBrush");
        var colorKey=color.ToString();
        var iconKey=_model.PlaybackButtonLabel+"|"+colorKey;
        if (colorKey!=_transportColorKey)
        {
            _transportColorKey=colorKey;
            _previous.ImageSource=Icon("M1,1 L3,1 3,15 1,15 Z M15,1 L4,8 15,15 Z",color);
            _next.ImageSource=Icon("M13,1 L15,1 15,15 13,15 Z M1,1 L12,8 1,15 Z",color);
        }
        if (iconKey!=_iconKey)
        {
            _iconKey=iconKey;
            _play.ImageSource=Icon(_model.PlaybackButtonIsPlaying ? "M3,1 L7,1 7,15 3,15 Z M9,1 L13,1 13,15 9,15 Z" : "M3,1 L15,8 3,15 Z",color);
        }
        var key=BitmapContentKey(state)+"|"+Brush("SurfaceBrush")+"|"+Brush("TextPrimaryBrush")+"|"+Brush("TextSecondaryBrush")+"|"+colorKey;
        if (key==_lastKey) return;
        _lastKey=key;
        TaskbarThumbnail.Invalidate(_source.Handle);
    }
    public static string BitmapContentKey(PlaybackPresentation state) =>
        state.EditContext+"|"+state.Raw.Lyric.LineIndex+"|"+state.Title+"|"+state.Artist+"|"+
        (state.Health==LyricsHealth.Ready ? state.LyricText : state.StatusTitle)+"|"+CurrentTranslation(state);
    public static bool WindowsModeIsLight(object? setting) => setting is int value && value!=0;
    private void RefreshShellColors()
    {
        bool light=false;
        try
        {
            using var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            light=WindowsModeIsLight(key?.GetValue("SystemUsesLightTheme"));
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.IO.IOException) { }
        if (_shellLight==light) return;
        _shellLight=light;
        _shellColors=new ResourceDictionary { Source=new Uri(
            $"/TaskbarLyrics.App;component/Themes/Colors.{(light ? "Light" : "Dark")}.xaml",UriKind.Relative) };
    }
    private void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_window.Dispatcher.HasShutdownStarted) return;
        _window.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) return;
            RefreshShellColors();
            Refresh();
        }));
    }
    private Brush Brush(string key)
    {
        if (key is "SurfaceBrush" or "TextPrimaryBrush" or "TextSecondaryBrush")
        {
            if (SystemParameters.HighContrast)
                return key=="SurfaceBrush" ? System.Windows.SystemColors.WindowBrush : System.Windows.SystemColors.WindowTextBrush;
            return key=="SurfaceBrush" ? Brushes.Transparent : (Brush)_shellColors[key];
        }
        return _window.TryFindResource(key) as Brush ?? Brushes.White;
    }
    private static ImageSource Icon(string path, Brush color)
    {
        var group=new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent,null,new RectangleGeometry(new Rect(0,0,16,16))));
        // Use the complete native icon slot instead of leaving a one-pixel inset.
        var geometry=Geometry.Parse(path).Clone();
        geometry.Transform=new ScaleTransform(8.0/7,8.0/7,8,8);
        group.Children.Add(new GeometryDrawing(color,null,geometry));
        var image=new DrawingImage(group);
        if (image.CanFreeze) image.Freeze();
        return image;
    }
    private IntPtr Hook(IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        if (message != 0x0323) return IntPtr.Zero; // Only the taskbar thumbnail; never draw a desktop Peek bitmap.
        var width=(int)((l.ToInt64() >> 16) & 0xffff); var height=(int)(l.ToInt64() & 0xffff);
        if (width<=0 || height<=0) return IntPtr.Zero;
        try
        {
            var taskbar=NativeMethods.FindWindow(TaskbarLocator.PrimaryTaskbarClassName,null);
            var dpi=taskbar==IntPtr.Zero?NativeMethods.GetDpiForWindow(h):NativeMethods.GetDpiForWindow(taskbar);
            var dpiScale=dpi>0?dpi/96.0:1;
            var bitmap=Render(_model.State, _model.PlaybackButtonLabel, width,height,Brush("SurfaceBrush"),Brush("TextPrimaryBrush"),Brush("TextSecondaryBrush"),Brush("PlaybackIndicatorBrush"),dpiScale:dpiScale);
            var pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4]; bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);
            var result=TaskbarThumbnail.Set(h,bitmap.PixelWidth,bitmap.PixelHeight,pixels);
            handled=result>=0;
            var sizeKey=$"{width}x{height}->{bitmap.PixelWidth}x{bitmap.PixelHeight}@{dpi}:{result}";
            if(sizeKey!=_lastSize)
            {
                _lastSize=sizeKey;
                App.Logger?.Info($"Taskbar thumbnail request={width}x{height} bitmap={bitmap.PixelWidth}x{bitmap.PixelHeight} dpi={dpi} result=0x{result:X8}");
            }
        }
        catch (Exception ex) { App.Logger?.Error("Taskbar playback thumbnail",ex); }
        return IntPtr.Zero;
    }
    public static RenderTargetBitmap Render(PlaybackPresentation state,string action,int maxWidth,int maxHeight,Brush background,Brush foreground,Brush muted,Brush accent,double elapsedMs=0,bool animate=true,double dpiScale=1)
    {
        const double designWidth=180, designHeight=150;
        const double contentWidth=148, contentLeft=(designWidth-contentWidth)/2;
        FormattedText Format(string value,double size,Brush brush,bool bold=false) => new(
            value,CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI, Segoe UI"),FontStyles.Normal,bold?FontWeights.SemiBold:FontWeights.Normal,FontStretches.Normal),size,brush,1)
            {MaxTextWidth=contentWidth};
        var header=Format(state.HasTrack ? state.Title + (string.IsNullOrWhiteSpace(state.Artist) ? "" : " · " + state.Artist) : "等待播放",13,foreground);
        header.MaxTextHeight=18; header.Trimming=TextTrimming.CharacterEllipsis;
        var original=Format(state.HasTrack ? (state.Health==LyricsHealth.Ready ? state.LyricText : state.StatusTitle) : "在播放器中播放歌曲",20,accent,true);
        var translation=CurrentTranslation(state);
        var translated=string.IsNullOrWhiteSpace(translation)?null:Format(translation,13,muted);
        // Fixed static card: fit long text into reserved regions; never animate the bitmap.
        // The DWM request is a maximum, not a canvas to pad out.
        // Submit the compact card itself, with opaque physical-pixel edges.
        var nominalScale=double.IsFinite(dpiScale) && dpiScale>0?dpiScale:1;
        var scale=Math.Min(nominalScale,Math.Min(maxWidth/designWidth,maxHeight/designHeight));
        var width=Math.Max(1,(int)Math.Floor(designWidth*scale));
        var height=Math.Max(1,(int)Math.Floor(designHeight*scale));
        var visual=new DrawingVisual();
        using(var dc=visual.RenderOpen())
        {
            // Preserve premultiplied alpha so the shell can composite its own background.
            dc.DrawRectangle(background,null,new Rect(0,0,width,height));
            dc.PushClip(new RectangleGeometry(new Rect(0,0,width,height)));
            dc.PushTransform(new TranslateTransform((width-designWidth*scale)/2,(height-designHeight*scale)/2));
            dc.PushTransform(new ScaleTransform(scale,scale));
            dc.DrawText(header,new Point(contentLeft,6));
            void DrawBlock(FormattedText text, Rect viewport, double size, double minimumSize)
            {
                while (text.Height>viewport.Height && size>minimumSize)
                    text.SetFontSize(size=Math.Max(minimumSize,size-0.5));
                text.MaxTextHeight=viewport.Height;
                text.Trimming=TextTrimming.CharacterEllipsis;
                dc.PushClip(new RectangleGeometry(viewport));
                dc.DrawText(text,new Point(viewport.Left,viewport.Top));
                dc.Pop();
            }
            DrawBlock(original,new Rect(contentLeft,30,contentWidth,translated is null?112:72),20,12);
            if (translated is not null) DrawBlock(translated,new Rect(contentLeft,108,contentWidth,34),13,10);
            dc.Pop();
            dc.Pop();
            dc.Pop();
        }
        var result=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32); result.Render(visual); result.Freeze(); return result;
    }
    public void Dispose() { _disposed=true; SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged; _timer.Stop(); _source.RemoveHook(Hook); }
}
