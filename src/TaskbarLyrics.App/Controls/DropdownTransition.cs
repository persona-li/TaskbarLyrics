using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TaskbarLyrics.App.Controls;

// Owns the visible lifetime separately from the requested open state. Closing keeps the
// surface alive for its reverse animation, with pointer input already switched off.
internal static class DropdownTransition
{
    private sealed class SurfaceState
    {
        public long Revision;
        public long Starts;
        public Storyboard? Storyboard;
        public bool Prepared;
        public bool Inline;
        public double NaturalHeight;
        public double OriginalMaxHeight;
        public double OriginalMinHeight;
        public bool ChangingVisibility;
        public Geometry? OriginalClip;
        public Action? Complete;
        public Popup? Popup;
    }
    private sealed class PopupState
    {
        public FrameworkElement? Owner;
        public bool TargetOpen;
    }
    private static readonly ConditionalWeakTable<FrameworkElement, SurfaceState> Surfaces = new();
    private static readonly ConditionalWeakTable<Popup, PopupState> Popups = new();
    private static readonly ConditionalWeakTable<FrameworkElement, Popup> OwnedPopups = new();
    private static readonly DependencyProperty ProgressProperty = DependencyProperty.RegisterAttached(
        "Progress", typeof(double), typeof(DropdownTransition), new PropertyMetadata(1d, ProgressChanged));

    public static long GetStartCount(FrameworkElement element) => Surfaces.TryGetValue(element, out var state) ? state.Starts : 0;
    public static Storyboard? GetStoryboard(FrameworkElement element) => Surfaces.TryGetValue(element, out var state) ? state.Storyboard : null;

    public static void SetPopupOpen(Popup popup, bool open)
    {
        if (!Popups.TryGetValue(popup, out var state))
        {
            state = new PopupState(); Popups.Add(popup, state);
            popup.Loaded += PopupLoaded;
            popup.Unloaded += PopupUnloaded;
            popup.Closed += PopupClosed;
        }
        state.TargetOpen = open;
        AttachOwner(popup, state);
        UpdatePopup(popup, state, true);
    }
    private static void AttachOwner(Popup popup, PopupState state)
    {
        var owner = popup.TemplatedParent as FrameworkElement ?? popup.PlacementTarget as FrameworkElement;
        if (ReferenceEquals(owner, state.Owner)) return;
        DetachOwner(popup, state);
        state.Owner = owner;
        if (owner is null) return;
        OwnedPopups.Remove(owner); OwnedPopups.Add(owner, popup);
        owner.Unloaded += OwnerUnloaded;
        owner.IsVisibleChanged += OwnerVisibilityChanged;
    }
    private static void DetachOwner(Popup popup, PopupState state)
    {
        if (state.Owner is not { } owner) return;
        owner.Unloaded -= OwnerUnloaded;
        owner.IsVisibleChanged -= OwnerVisibilityChanged;
        if (OwnedPopups.TryGetValue(owner, out var existing) && ReferenceEquals(existing, popup)) OwnedPopups.Remove(owner);
        state.Owner = null;
    }
    private static void PopupLoaded(object sender, RoutedEventArgs e)
    {
        var popup = (Popup)sender;
        if (!Popups.TryGetValue(popup, out var state)) return;
        AttachOwner(popup, state);
        UpdatePopup(popup, state, false);
    }
    private static void PopupUnloaded(object sender, RoutedEventArgs e)
    {
        var popup = (Popup)sender;
        if (!Popups.TryGetValue(popup, out var state)) return;
        HidePopup(popup, state, cancelRequest: true);
        DetachOwner(popup, state);
    }
    private static void PopupClosed(object? sender, EventArgs e)
    {
        if (sender is not Popup popup || popup.IsOpen || !Popups.TryGetValue(popup, out var state)) return;
        if (popup.Child is FrameworkElement surface) Reset(surface);
        if (state.TargetOpen && state.Owner is System.Windows.Controls.ComboBox combo)
            combo.SetCurrentValue(System.Windows.Controls.ComboBox.IsDropDownOpenProperty, false);
    }
    private static void OwnerUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement owner && OwnedPopups.TryGetValue(owner, out var popup) && Popups.TryGetValue(popup, out var state))
        {
            HidePopup(popup, state, cancelRequest: true);
            DetachOwner(popup, state);
        }
    }
    private static void OwnerVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!(bool)e.NewValue && sender is FrameworkElement owner && OwnedPopups.TryGetValue(owner, out var popup) && Popups.TryGetValue(popup, out var state))
            HidePopup(popup, state, cancelRequest: true);
    }
    private static void HidePopup(Popup popup, PopupState state, bool cancelRequest)
    {
        if (popup.Child is FrameworkElement surface) Reset(surface);
        popup.SetCurrentValue(Popup.IsOpenProperty, false);
        if (cancelRequest && state.Owner is System.Windows.Controls.ComboBox combo)
            combo.SetCurrentValue(System.Windows.Controls.ComboBox.IsDropDownOpenProperty, false);
    }
    private static void UpdatePopup(Popup popup, PopupState state, bool animate)
    {
        if (popup.Child is not FrameworkElement surface) return;
        var owner = state.Owner ?? popup;
        Surfaces.GetOrCreateValue(surface).Popup = popup;
        if (!state.TargetOpen)
        {
            if (!popup.IsOpen) { Reset(surface); return; }
            Transition(surface, owner, false, false, animate, () => HidePopup(popup, state, false));
            return;
        }
        if (state.Owner is { IsLoaded: true, IsVisible: false }) { HidePopup(popup, state, true); return; }
        var hidden = !popup.IsOpen;
        popup.SetCurrentValue(Popup.IsOpenProperty, true);
        Transition(surface, owner, true, hidden, animate, () => { });
    }

    public static void SetContentOpen(FrameworkElement element, bool open)
    {
        var state = Surfaces.GetOrCreateValue(element);
        if (!state.Inline)
        {
            state.Inline = true;
            element.Loaded += ContentLoaded;
            element.Unloaded += ContentUnloaded;
            element.IsVisibleChanged += ContentVisibilityChanged;
        }
        UpdateContent(element, open, element.IsLoaded);
    }
    private static void ContentLoaded(object sender, RoutedEventArgs e) => UpdateContent((FrameworkElement)sender, Motion.GetContentOpen((FrameworkElement)sender), false);
    private static void ContentUnloaded(object sender, RoutedEventArgs e) => UpdateContent((FrameworkElement)sender, Motion.GetContentOpen((FrameworkElement)sender), false);
    private static void ContentVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        if (!(bool)e.NewValue && Surfaces.TryGetValue(element, out var state) && !state.ChangingVisibility)
            UpdateContent(element, Motion.GetContentOpen(element), false);
    }
    private static void SetVisibility(FrameworkElement element, Visibility visibility)
    {
        var state = Surfaces.GetOrCreateValue(element);
        state.ChangingVisibility = true;
        try { element.SetCurrentValue(UIElement.VisibilityProperty, visibility); }
        finally { state.ChangingVisibility = false; }
    }
    private static void UpdateContent(FrameworkElement element, bool open, bool animate)
    {
        var hidden = element.Visibility != Visibility.Visible;
        if (open) SetVisibility(element, Visibility.Visible);
        Transition(element, element, open, hidden, animate, () => SetVisibility(element, open ? Visibility.Visible : Visibility.Collapsed));
    }

    public static void Reduce(FrameworkElement element)
    {
        if (OwnedPopups.TryGetValue(element, out var popup) && Popups.TryGetValue(popup, out var popupState)) UpdatePopup(popup, popupState, false);
        if (!Surfaces.TryGetValue(element, out var state)) return;
        if (state.Inline) UpdateContent(element, Motion.GetContentOpen(element), false);
        else if (state.Popup is { } ownPopup && Popups.TryGetValue(ownPopup, out var ownState)) UpdatePopup(ownPopup, ownState, false);
    }

    private static void Transition(FrameworkElement element, FrameworkElement owner, bool open, bool hidden, bool animate, Action completed)
    {
        var state = Surfaces.GetOrCreateValue(element);
        var from = state.Prepared ? (double)element.GetValue(ProgressProperty) : hidden ? 0d : 1d;
        StopClock(element, state);
        state.Complete = completed;
        if (!animate || !owner.IsLoaded || !owner.IsVisible || !Motion.Allowed(owner) || (!open && from <= .001))
        {
            Finish(element, state);
            return;
        }
        if (!state.Prepared)
        {
            state.OriginalClip = element.Clip;
            if (state.Inline)
            {
                state.OriginalMaxHeight = element.MaxHeight;
                state.OriginalMinHeight = element.MinHeight;
                element.Measure(new System.Windows.Size(Math.Max(1, element.ActualWidth > 0 ? element.ActualWidth : ((element.Parent as FrameworkElement)?.ActualWidth ?? 600)), double.PositiveInfinity));
                state.NaturalHeight = Math.Max(0, element.DesiredSize.Height - element.Margin.Top - element.Margin.Bottom);
                element.SetCurrentValue(FrameworkElement.MinHeightProperty, 0d);
            }
            state.Prepared = true;
        }
        // A zero-duration held clock masks input without writing a local value or changing a
        // binding. Reading IsHitTestVisible here would capture false inherited from a closing
        // parent and permanently disable a nested disclosure when it reopens.
        element.BeginAnimation(UIElement.IsHitTestVisibleProperty, null);
        if (!open)
        {
            var gate = new BooleanAnimationUsingKeyFrames
            {
                Duration = TimeSpan.Zero,
                FillBehavior = FillBehavior.HoldEnd,
                KeyFrames = { new DiscreteBooleanKeyFrame(false, KeyTime.FromTimeSpan(TimeSpan.Zero)) }
            }.CreateClock();
            element.ApplyAnimationClock(UIElement.IsHitTestVisibleProperty, gate);
            gate.Controller!.SeekAlignedToLastTick(TimeSpan.Zero, TimeSeekOrigin.BeginTime);
        }
        element.SetCurrentValue(UIElement.ClipProperty, new RectangleGeometry());
        element.RenderTransform = new TranslateTransform();
        element.SetCurrentValue(ProgressProperty, from);
        RenderProgress(element, state, from);
        var revision = state.Revision;
        void Begin()
        {
            if (revision != state.Revision) return;
            if (!owner.IsVisible || !Motion.Allowed(owner)) { Finish(element, state); return; }
            element.UpdateLayout();
            if (!element.IsVisible || element.ActualWidth <= 0 || (!state.Inline && element.ActualHeight <= 0)) { Finish(element, state); return; }
            var target = open ? 1d : 0d;
            var motion = new DoubleAnimation(from, target, Motion.ExpandDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(motion, element);
            Storyboard.SetTargetProperty(motion, new PropertyPath(ProgressProperty));
            var storyboard = new Storyboard { FillBehavior = FillBehavior.HoldEnd };
            storyboard.Children.Add(motion);
            storyboard.Completed += (_, _) => { if (revision == state.Revision) Finish(element, state); };
            state.Storyboard = storyboard;
            storyboard.Begin(element, HandoffBehavior.SnapshotAndReplace, true);
            state.Starts++;
        }
        if (hidden || !element.IsVisible || element.ActualHeight <= 0) element.Dispatcher.BeginInvoke(Begin, DispatcherPriority.Loaded);
        else Begin();
    }
    private static void ProgressChanged(DependencyObject o, DependencyPropertyChangedEventArgs e)
    {
        if (o is FrameworkElement element && Surfaces.TryGetValue(element, out var state) && state.Prepared)
            RenderProgress(element, state, (double)e.NewValue);
    }
    private static void RenderProgress(FrameworkElement element, SurfaceState state, double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        element.Opacity = progress;
        if (state.Inline) element.SetCurrentValue(FrameworkElement.MaxHeightProperty, state.NaturalHeight * progress);
        if (element.RenderTransform is TranslateTransform transform) transform.Y = -4 * (1 - progress);
        if (element.Clip is RectangleGeometry clip)
            clip.Rect = new Rect(0, 0, Math.Max(0, element.ActualWidth), Math.Max(0, state.Inline ? state.NaturalHeight * progress : element.ActualHeight * progress));
    }
    private static void StopClock(FrameworkElement element, SurfaceState state)
    {
        state.Revision++;
        state.Storyboard?.Remove(element); state.Storyboard = null;
        element.BeginAnimation(ProgressProperty, null);
    }
    private static void Finish(FrameworkElement element, SurfaceState state)
    {
        var complete = state.Complete;
        Reset(element);
        complete?.Invoke();
    }
    private static void Reset(FrameworkElement element)
    {
        var state = Surfaces.GetOrCreateValue(element);
        StopClock(element, state);
        state.Complete = null;
        if (state.Prepared)
        {
            element.SetCurrentValue(UIElement.ClipProperty, state.OriginalClip);
            if (state.Inline) { element.SetCurrentValue(FrameworkElement.MaxHeightProperty, state.OriginalMaxHeight); element.SetCurrentValue(FrameworkElement.MinHeightProperty, state.OriginalMinHeight); }
            element.BeginAnimation(UIElement.IsHitTestVisibleProperty, null);
            state.OriginalClip = null;
            state.Prepared = false;
        }
        element.Opacity = 1;
        element.RenderTransform = Transform.Identity;
    }
}
