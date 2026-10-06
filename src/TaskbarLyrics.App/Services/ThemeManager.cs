using System.Windows;
using Microsoft.Win32;

namespace TaskbarLyrics.App.Services;

/// <summary>
/// Swaps the application color+brush dictionary between Dark and Light.
/// Brushes must live inside Colors.*.xaml so DynamicResource consumers update.
/// </summary>
public static class ThemeManager
{
    private const string DarkSource = "/TaskbarLyrics.App;component/Themes/Colors.Dark.xaml";
    private const string LightSource = "/TaskbarLyrics.App;component/Themes/Colors.Light.xaml";

    public static string CurrentPreference { get; private set; } = "Dark";
    public static bool IsHighContrast { get; private set; }
    public static bool IsDark { get; private set; } = true;

    public static event Action? ThemeChanged;

    public static void Apply(string? preference, bool? highContrastOverride = null, bool? systemLightOverride = null)
    {
        var nextPreference = preference switch
        {
            "Light" => "Light",
            "FollowSystem" or "Glass" => "FollowSystem",
            _ => "Dark"
        };
        var wantDark = nextPreference == "Dark" ||
                       (nextPreference == "FollowSystem" && !(systemLightOverride ?? SystemUsesLightTheme()));

        var highContrast = highContrastOverride ?? SystemParameters.HighContrast;
        var contrastChanged = IsHighContrast != highContrast;
        IsHighContrast = highContrast;
        var preferenceChanged = !string.Equals(CurrentPreference, nextPreference, StringComparison.Ordinal);
        var modeChanged = IsDark != wantDark || contrastChanged;
        CurrentPreference = nextPreference;

        var resources = Application.Current?.Resources.MergedDictionaries;
        if (resources is null)
        {
            IsDark = wantDark;
            if (preferenceChanged || modeChanged)
            {
                ThemeChanged?.Invoke();
            }

            return;
        }

        var source = new Uri(highContrast ? "/TaskbarLyrics.App;component/Themes/Colors.HighContrast.xaml" : wantDark ? DarkSource : LightSource, UriKind.Relative);
        var current = resources.FirstOrDefault(IsThemeDictionary);

        if (current is null)
        {
            resources.Insert(0, new ResourceDictionary { Source = source });
            modeChanged = true;
        }
        else if (modeChanged || !IsSameThemeSource(current, source))
        {
            resources[resources.IndexOf(current)] = new ResourceDictionary { Source = source };
            modeChanged = true;
        }

        IsDark = wantDark;

        if (preferenceChanged || modeChanged)
        {
            ThemeChanged?.Invoke();
        }
    }

    /// <summary>Re-evaluate FollowSystem when Windows theme changes.</summary>
    public static void RefreshSystemThemeIfNeeded()
    {
        Apply(CurrentPreference);
    }

    public static void StartWatchingSystemTheme()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
    }

    public static void StopWatchingSystemTheme()
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
    }

    private static void OnSystemParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
            Application.Current?.Dispatcher.BeginInvoke(RefreshSystemThemeIfNeeded);
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility))
        {
            return;
        }

        var app = Application.Current;
        if (app?.Dispatcher is null)
        {
            return;
        }

        app.Dispatcher.BeginInvoke(RefreshSystemThemeIfNeeded);
    }

    private static bool IsThemeDictionary(ResourceDictionary d) =>
        d.Source?.OriginalString.EndsWith("Colors.HighContrast.xaml", StringComparison.OrdinalIgnoreCase) == true ||
        d.Source?.OriginalString.EndsWith("Colors.Dark.xaml", StringComparison.OrdinalIgnoreCase) == true ||
        d.Source?.OriginalString.EndsWith("Colors.Light.xaml", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsSameThemeSource(ResourceDictionary current, Uri source) =>
        string.Equals(current.Source?.OriginalString, source.OriginalString, StringComparison.OrdinalIgnoreCase);

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }
}
