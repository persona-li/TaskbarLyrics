using TaskbarLyrics.App.Controls;
using TaskbarLyrics.App.Services;
using TaskbarLyrics.Core.Models;
namespace TaskbarLyrics.App.Presentation;

public enum SettingsPage { Overview, Appearance, Synchronization, General, Data, About }
public enum LyricsHealth { Disconnected, Waiting, Loading, Ready, NeedsRematch, NoLyrics, Instrumental, Error }
public sealed record PlaybackPresentation(OverlayUiState Raw, LyricsHealth Health, string StatusTitle, string StatusHint)
{
    public bool HasTrack => Raw.SessionConnected && !string.IsNullOrWhiteSpace(Raw.Track?.Title);
    public bool CanAdjustTrack => HasTrack && !string.IsNullOrEmpty(Raw.CacheKey);
    public string TrackOffsetHint => CanAdjustTrack ? "" : !HasTrack ? "在播放器中播放歌曲"
        : Health == LyricsHealth.Loading ? "正在获取歌词…" : "先选择歌词，再校准本曲";
    public string Title => HasTrack ? Raw.Track!.Title : "暂无播放";
    public string Artist => HasTrack ? Raw.Track!.Artist : "";
    public string Album => HasTrack ? Raw.Track?.Album ?? "" : "";
    public bool NeedsAttention => HasTrack && Health is (LyricsHealth.Error or LyricsHealth.NoLyrics or LyricsHealth.NeedsRematch);
    public bool HasAttentionHint => NeedsAttention && !string.IsNullOrEmpty(StatusHint);
    public bool IsPlaying => HasTrack && Raw.Status == PlaybackStatus.Playing;
    public string PlaybackLabel => !HasTrack ? "等待播放" : Raw.Status switch { PlaybackStatus.Playing => "正在播放", PlaybackStatus.Paused => "已暂停", PlaybackStatus.Stopped => "已停止", _ => "等待播放" };
    public string ModeLabel => !HasTrack ? "暂无歌词" : Raw.LyricsMode switch { LyricsMode.Qrc => "逐字", LyricsMode.Lrc => "逐行", _ => "暂无歌词" };
    public string SourceLabel => !HasTrack ? "等待获取" : Raw.LyricsSource switch { LyricsSourceKind.Cache => "本地", LyricsSourceKind.Network => "在线", _ => "等待获取" };
    public string MatchLabel => !HasTrack ? "尚未匹配" : Raw.MatchSource == "Manual" ? "手动匹配" : Raw.MatchSource == "Automatic" ? "自动匹配" : "尚未匹配";
    public string Position => !HasTrack ? "00:00" : FormatTime(Raw.Clock.EstimatedPosition);
    public string Duration => !HasTrack ? "—:—" : Raw.Track?.Duration is TimeSpan d && d > TimeSpan.Zero ? FormatTime(d) : "—:—";
    public string TimeLabel => Position + " / " + Duration;
    public double Progress => !HasTrack ? 0 : Raw.Track?.Duration?.TotalSeconds > 0 ? Math.Clamp(Raw.Clock.EstimatedPosition.TotalSeconds / Raw.Track.Duration.Value.TotalSeconds * 100, 0, 100) : 0;
    public string LyricText => !HasTrack ? StatusHint : string.IsNullOrEmpty(Raw.DisplayText) ? (Health == LyricsHealth.Ready ? "♪ 间奏" : StatusHint) : Raw.DisplayText;
    // Visual identity excludes lyric cache resolution, reload generations and transport status.
    public object TrackVisualKey => (Raw.SessionConnected, Raw.Track?.Title, Raw.Track?.Artist);
    public string EditContext => Raw.Generation + ":" + Raw.CacheKey;
    public string OffsetLabel => $"{Raw.GlobalOffsetMs:+0;-0;0} ms 全局  +  {Raw.TrackOffsetMs:+0;-0;0} ms 单曲";
    public string EffectiveLabel => $"{Raw.EffectiveOffsetMs:+0;-0;0} ms";
    public string ActualTimingEffect => Raw.EffectiveOffsetMs == 0 ? "歌词无偏移" : "歌词" + TimingSummary(Raw.EffectiveOffsetMs);
    public string GlobalTimingSummary => Raw.GlobalOffsetMs == 0 ? "未调整" : TimingSummary(Raw.GlobalOffsetMs);
    private static string TimingSummary(long milliseconds) => milliseconds > 0
        ? $"提前 {milliseconds} ms" : $"延后 {-(decimal)milliseconds} ms";
    public KaraokeRenderState Render => new(Raw.DisplayText, Raw.CurrentLineWords, Raw.WordIndex, Raw.WordProgress, true, Raw.StaticLineOnly);
    public static string FormatTime(TimeSpan time) => $"{(int)Math.Max(0, time.TotalMinutes):00}:{Math.Max(0, time.Seconds):00}";
    public static PlaybackPresentation From(OverlayUiState s)
    {
        var h = !s.SessionConnected ? LyricsHealth.Disconnected : string.IsNullOrWhiteSpace(s.Track?.Title) ? LyricsHealth.Waiting :
            s.LyricsLoading ? LyricsHealth.Loading : s.IsInstrumental ? LyricsHealth.Instrumental :
            s.NeedsRematch ? LyricsHealth.NeedsRematch : s.HasLyrics ? LyricsHealth.Ready : s.NoTimedLyrics ? LyricsHealth.NoLyrics :
            !string.IsNullOrEmpty(s.Error) ? LyricsHealth.Error : LyricsHealth.NoLyrics;
        var (title, hint) = h switch
        {
            LyricsHealth.Disconnected => ("未连接", "在 QQ 音乐中播放歌曲"),
            LyricsHealth.Waiting => ("等待播放", "在 QQ 音乐中播放歌曲"),
            LyricsHealth.Loading => ("加载中", "正在获取歌词…"),
            LyricsHealth.Ready => ("已就绪", ""),
            LyricsHealth.NeedsRematch => ("需要匹配", ""),
            LyricsHealth.Instrumental => ("纯音乐", ""),
            LyricsHealth.Error => ("加载失败", "重试加载，或选择其他版本"),
            _ => ("暂无歌词", "此版本没有可用歌词")
        };
        return new(s, h, title, hint);
    }
}

public static class OffsetAdjustment
{
    public static long Add(long current, long delta) => (long)Math.Clamp((decimal)current + delta, -5000, 5000);
    public static bool Matches(long expected, string? key, long actual, string? actualKey) => expected == actual && !string.IsNullOrEmpty(key) && key == actualKey;
}
