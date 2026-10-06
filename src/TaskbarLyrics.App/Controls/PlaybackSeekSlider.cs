using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace TaskbarLyrics.App.Controls;

/// <summary>Displays playback progress and commits a seek only when a user gesture ends.</summary>
public class PlaybackSeekSlider : Slider
{
    public static readonly DependencyProperty PositionSecondsProperty = DependencyProperty.Register(
        nameof(PositionSeconds), typeof(double), typeof(PlaybackSeekSlider),
        new FrameworkPropertyMetadata(0d, OnPositionChanged));
    public static readonly DependencyProperty DurationSecondsProperty = DependencyProperty.Register(
        nameof(DurationSeconds), typeof(double), typeof(PlaybackSeekSlider),
        new FrameworkPropertyMetadata(0d, OnDurationChanged));
    public static readonly DependencyProperty TrackKeyProperty = DependencyProperty.Register(
        nameof(TrackKey), typeof(string), typeof(PlaybackSeekSlider),
        new FrameworkPropertyMetadata(string.Empty, OnTrackChanged));
    public static readonly DependencyProperty SeekCommandProperty = DependencyProperty.Register(
        nameof(SeekCommand), typeof(ICommand), typeof(PlaybackSeekSlider),
        new FrameworkPropertyMetadata(null, OnCommandChanged));
    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(
        nameof(IsPlaying), typeof(bool), typeof(PlaybackSeekSlider), new PropertyMetadata(false, ResetSmoothing));
    public static readonly DependencyProperty SmoothPlaybackProperty = DependencyProperty.Register(
        nameof(SmoothPlayback), typeof(bool), typeof(PlaybackSeekSlider), new PropertyMetadata(false, ResetSmoothing));
    public static readonly DependencyProperty IsSeekEnabledProperty = DependencyProperty.Register(
        nameof(IsSeekEnabled), typeof(bool), typeof(PlaybackSeekSlider), new PropertyMetadata(true, SeekAvailabilityChanged));
    private static readonly DependencyPropertyKey CanSeekPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(CanSeek), typeof(bool), typeof(PlaybackSeekSlider), new PropertyMetadata(false));
    public static readonly DependencyProperty CanSeekProperty = CanSeekPropertyKey.DependencyProperty;

    private enum Gesture { None, Mouse, Keyboard }
    private Gesture _gesture;
    private string? _editingTrack;
    private readonly HashSet<Key> _heldSeekKeys = [];

    static PlaybackSeekSlider() => DefaultStyleKeyProperty.OverrideMetadata(
        typeof(PlaybackSeekSlider), new FrameworkPropertyMetadata(typeof(Slider)));

    public PlaybackSeekSlider()
    {
        // Derived controls do not automatically use an implicit style keyed to Slider.
        SetResourceReference(StyleProperty, typeof(Slider));
        Minimum = 0;
        Maximum = 0;
        SmallChange = 1;
        LargeChange = 5;
        IsMoveToPointEnabled = true;
        AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler(CaptureTrackClick), true);
        IsEnabledChanged += (_, _) => RefreshAvailability();
        Loaded += (_, _) => { FollowPlayback(); RefreshAvailability(); };
        IsVisibleChanged += (_, _) => FollowPlayback();
        Unloaded += (_, _) => { CancelGesture(); _heldSeekKeys.Clear(); };
    }

    public bool IsPlaying { get => (bool)GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    public bool SmoothPlayback { get => (bool)GetValue(SmoothPlaybackProperty); set => SetValue(SmoothPlaybackProperty, value); }
    public bool IsSeekEnabled { get => (bool)GetValue(IsSeekEnabledProperty); set => SetValue(IsSeekEnabledProperty, value); }
    public bool CanSeek => (bool)GetValue(CanSeekProperty);

    public double PositionSeconds
    {
        get => (double)GetValue(PositionSecondsProperty);
        set => SetValue(PositionSecondsProperty, value);
    }
    public double DurationSeconds
    {
        get => (double)GetValue(DurationSecondsProperty);
        set => SetValue(DurationSecondsProperty, value);
    }
    public string TrackKey
    {
        get => (string)GetValue(TrackKeyProperty);
        set => SetValue(TrackKeyProperty, value);
    }
    public ICommand? SeekCommand
    {
        get => (ICommand?)GetValue(SeekCommandProperty);
        set => SetValue(SeekCommandProperty, value);
    }

    protected override bool IsEnabledCore => base.IsEnabledCore && HasDuration;
    private bool HasDuration => double.IsFinite(DurationSeconds) && DurationSeconds > 0;

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        RefreshAvailability();
        if (!CanSeek) { e.Handled = true; return; }
        BeginGesture(Gesture.Mouse);
        base.OnPreviewMouseLeftButtonDown(e);
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        // Preview runs before Thumb releases mouse capture at the end of its drag.
        CompleteGesture(Gesture.Mouse);
        base.OnPreviewMouseLeftButtonUp(e);
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_gesture == Gesture.Mouse && !IsMouseCaptureWithin) CancelGesture();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_gesture == Gesture.Mouse && ReferenceEquals(Mouse.Captured, this)
            && GetTemplateChild("PART_Track") is Track track && e.LeftButton == MouseButtonState.Pressed)
        {
            var value = track.ValueFromPoint(e.GetPosition(track));
            if (double.IsFinite(value)) SetCurrentValue(ValueProperty, ClampPosition(value));
            e.Handled = true;
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        RefreshAvailability();
        if (IsSeekKey(e.Key) && !CanSeek) { e.Handled = true; return; }
        if (e.Key == Key.Escape && _gesture != Gesture.None)
        {
            CancelGesture();
            e.Handled = true;
            return;
        }
        if (IsSeekKey(e.Key) && _heldSeekKeys.Add(e.Key) && !e.IsRepeat) BeginGesture(Gesture.Keyboard);
        base.OnPreviewKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (IsSeekKey(e.Key))
        {
            _heldSeekKeys.Remove(e.Key);
            CompleteGesture(Gesture.Keyboard);
            if (!CanSeek) FollowPlayback();
        }
        base.OnKeyUp(e);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        CancelGesture();
        _heldSeekKeys.Clear();
        base.OnLostKeyboardFocus(e);
    }

    private void CaptureTrackClick(object sender, MouseButtonEventArgs e)
    {
        // Thumb captures itself; an empty-track click needs capture to receive an
        // eventual release outside the control as well.
        if (_gesture == Gesture.Mouse && !IsMouseCaptureWithin) CaptureMouse();
    }

    private void BeginGesture(Gesture gesture)
    {
        if (!CanSeek || _gesture != Gesture.None) return;
        var shown = Value;
        BeginAnimation(ValueProperty, null);
        SetValue(ValueProperty, shown);
        _gesture = gesture;
        _editingTrack = TrackKey;
    }

    private void CompleteGesture(Gesture expected)
    {
        if (_gesture != expected) return;
        var sameTrack = string.Equals(_editingTrack, TrackKey, StringComparison.Ordinal);
        var seconds = ClampPosition(Value);
        _gesture = Gesture.None;
        _editingTrack = null;
        ReleaseOwnedCapture();
        if (sameTrack && IsEnabled && IsSeekEnabled && HasDuration && SeekCommand is { } command && command.CanExecute(seconds))
            command.Execute(seconds);
        else
            FollowPlayback();
    }

    private void CancelGesture()
    {
        _gesture = Gesture.None;
        _editingTrack = null;
        var thumb = (GetTemplateChild("PART_Track") as Track)?.Thumb;
        if (thumb?.IsDragging == true) thumb.CancelDrag();
        ReleaseOwnedCapture();
        FollowPlayback();
    }

    private void ReleaseOwnedCapture()
    {
        if (ReferenceEquals(Mouse.Captured, this)) ReleaseMouseCapture();
    }

    private void FollowPlayback()
    {
        BeginAnimation(ValueProperty, null);
        if (_gesture == Gesture.None) SetValue(ValueProperty, ClampPosition(PositionSeconds));
    }

    private double ClampPosition(double value) => HasDuration && double.IsFinite(value)
        ? Math.Clamp(value, 0, DurationSeconds) : 0;

    private static bool IsSeekKey(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down
        or Key.Home or Key.End or Key.PageUp or Key.PageDown;

    public static bool ShouldSmooth(double previous, double next, double duration, bool playing, bool visible, bool animations) =>
        double.IsFinite(duration) && duration > 0 && double.IsFinite(previous) && double.IsFinite(next)
        && SmoothProgressBar.ShouldSmooth(previous / duration * 100, next / duration * 100, playing, visible, animations);

    private static void OnPositionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var slider = (PlaybackSeekSlider)d;
        slider.RefreshAvailability();
        if (slider._gesture != Gesture.None) return;
        var from = slider.Value;
        var target = slider.ClampPosition(slider.PositionSeconds);
        var smooth = slider.SmoothPlayback && ShouldSmooth((double)e.OldValue, target, slider.DurationSeconds,
            slider.IsPlaying, slider.IsLoaded && slider.IsVisible, Motion.Allowed(slider));
        slider.FollowPlayback();
        if (smooth)
            slider.BeginAnimation(ValueProperty, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(250))
            { FillBehavior = FillBehavior.Stop }, HandoffBehavior.SnapshotAndReplace);
    }

    private static void OnDurationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var slider = (PlaybackSeekSlider)d;
        slider.SetCurrentValue(MaximumProperty, slider.HasDuration ? slider.DurationSeconds : 0);
        slider.CoerceValue(IsEnabledProperty);
        slider.RefreshAvailability();
        if (!slider.HasDuration) slider.CancelGesture();
        else slider.FollowPlayback();
    }

    private static void OnTrackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PlaybackSeekSlider)d).CancelGesture();

    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var slider = (PlaybackSeekSlider)d;
        if (e.OldValue is ICommand oldCommand)
            WeakEventManager<ICommand, EventArgs>.RemoveHandler(oldCommand, nameof(ICommand.CanExecuteChanged), slider.CommandAvailabilityChanged);
        if (e.NewValue is ICommand newCommand)
            WeakEventManager<ICommand, EventArgs>.AddHandler(newCommand, nameof(ICommand.CanExecuteChanged), slider.CommandAvailabilityChanged);
        slider.CancelGesture();
        slider.RefreshAvailability();
    }

    private void CommandAvailabilityChanged(object? sender, EventArgs e) => RefreshAvailability();
    private void RefreshAvailability()
    {
        var allowed = IsEnabled && IsSeekEnabled && HasDuration && SeekCommand?.CanExecute(ClampPosition(PositionSeconds)) == true;
        SetValue(CanSeekPropertyKey, allowed);
        if (!allowed && _gesture != Gesture.None) CancelGesture();
    }
    private static void SeekAvailabilityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((PlaybackSeekSlider)d).RefreshAvailability();
    private static void ResetSmoothing(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((PlaybackSeekSlider)d).FollowPlayback();
}
