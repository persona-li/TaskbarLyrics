namespace TaskbarLyrics.App.Presentation;

public sealed class SettingsNavigation
{
    private readonly Stack<SettingsPage> _back = new();
    public SettingsPage Current { get; private set; } = SettingsPage.Overview;
    public bool CanGoBack => _back.Count > 0;
    public static SettingsPage Normalize(SettingsPage page) => page == SettingsPage.Data ? SettingsPage.General : page;
    public bool Navigate(SettingsPage page)
    {
        page = Normalize(page);
        if (page == Current) return false;
        _back.Push(Current);
        Current = page;
        return true;
    }
    public bool TryGoBack(out SettingsPage page)
    {
        if (!_back.TryPop(out page)) return false;
        page = Normalize(page);
        Current = page;
        return true;
    }
}
