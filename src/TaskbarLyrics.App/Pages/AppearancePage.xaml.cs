using System.Windows.Controls;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TaskbarLyrics.App.Controls;
namespace TaskbarLyrics.App.Pages;
public partial class AppearancePage : UserControl
{
    private int _section;
    public AppearancePage()
    {
        InitializeComponent();
        SizeChanged += (_, e) => Preview.Height = e.NewSize.Height < 520 ? 110 : 150;
        Unloaded += (_, _) => FinishTransition();
    }
    private void FinishTransition()
    {
        IndicatorTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        IndicatorTranslate.X = _section * 76;
        ContentTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        ContentTranslate.X = 0;
        SectionContent.BeginAnimation(OpacityProperty, null);
        SectionContent.Opacity = 1;
    }
    private void SectionChanged(object sender, RoutedEventArgs e)
    {
        if (SettingsScroll is null || sender is not System.Windows.Controls.RadioButton tab) return;
        var next = tab == ColorsTab ? 1 : tab == PositionTab ? 2 : 0;
        var previous = _section;
        var indicatorFrom = IndicatorTranslate.X;
        _section = next;
        SettingsScroll.ScrollToTop();
        FinishTransition();
        if (next == previous || !IsLoaded || !Motion.Allowed(this)) return;

        var duration = TimeSpan.FromMilliseconds(200);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        IndicatorTranslate.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(indicatorFrom, next * 76, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        // Tabs progress to the right; forward pages arrive from the right and vice versa.
        ContentTranslate.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(next > previous ? 20 : -20, 0, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });
        SectionContent.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, duration) { FillBehavior = FillBehavior.Stop });
    }
}
