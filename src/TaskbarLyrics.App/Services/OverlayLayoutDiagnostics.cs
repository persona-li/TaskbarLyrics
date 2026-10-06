namespace TaskbarLyrics.App.Services;

/// <summary>
/// Last measured overlay width decision — shown in Settings UI.
/// Updated by MainWindow.RepositionOverlay (UI thread).
/// </summary>
public static class OverlayLayoutDiagnostics
{
    private static readonly object Lock = new();

    public static OverlayLayoutSnapshot Current { get; private set; } = OverlayLayoutSnapshot.Empty;

    public static void Update(OverlayLayoutSnapshot snapshot)
    {
        lock (Lock)
        {
            Current = snapshot;
        }
    }

    public static OverlayLayoutSnapshot Get()
    {
        lock (Lock)
        {
            return Current;
        }
    }
}

public sealed record OverlayLayoutSnapshot(
    string Mode,
    string ModeDetail,
    int TaskbarWidthPx,
    int TaskbarLeftPx,
    int? StartLeftPx,
    int? FreeBandPx,
    int RequestedWidthPx,
    int FinalWidthPx,
    int SafetyRightPx,
    int OverlayLeftPx,
    int OverlayRightPx,
    string Source,
    DateTimeOffset UpdatedAt)
{
    public bool UsesFallbackWidth => TaskbarWidthPx > 0 && FreeBandPx is null;

    public static OverlayLayoutSnapshot Empty { get; } = new(
        Mode: "尚未测量",
        ModeDetail: "等待 Overlay 定位",
        TaskbarWidthPx: 0,
        TaskbarLeftPx: 0,
        StartLeftPx: null,
        FreeBandPx: null,
        RequestedWidthPx: 0,
        FinalWidthPx: 0,
        SafetyRightPx: 0,
        OverlayLeftPx: 0,
        OverlayRightPx: 0,
        Source: "",
        UpdatedAt: DateTimeOffset.MinValue);

    public string ToDisplayText()
    {
        var start = StartLeftPx?.ToString() ?? "—";
        var free = FreeBandPx?.ToString() ?? "—";
        var when = UpdatedAt == DateTimeOffset.MinValue
            ? "—"
            : UpdatedAt.ToLocalTime().ToString("HH:mm:ss");

        return
            $"当前模式：{Mode}\n" +
            $"{ModeDetail}\n\n" +
            $"任务栏宽度：{TaskbarWidthPx} px\n" +
            $"任务栏 Left：{TaskbarLeftPx}\n" +
            $"开始按钮 Left：{start}\n" +
            $"自由带宽（测得）：{free} px\n" +
            $"计算请求宽度：{RequestedWidthPx} px\n" +
            $"最终 Overlay 宽度：{FinalWidthPx} px\n" +
            $"安全右界：{SafetyRightPx}\n" +
            $"Overlay Left/Right：{OverlayLeftPx} / {OverlayRightPx}\n" +
            $"检测来源：{(string.IsNullOrEmpty(Source) ? "—" : Source)}\n" +
            $"更新时间：{when}";
    }
}
