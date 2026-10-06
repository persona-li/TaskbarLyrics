using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using TaskbarLyrics.App.Services;
using TaskbarLyrics.Core.Models;
using TaskbarLyrics.QQMusic.Models;
using TaskbarLyrics.QQMusic.Matching;
using TaskbarLyrics.QQMusic.Services;
namespace TaskbarLyrics.App.Presentation;
public interface IRematchBackend
{
    event Action? StateChanged;
    (TrackIdentity? Track, long Generation) Current { get; }
    Task<(IReadOnlyList<QQSongCandidate> Items, bool HasMore, string? Error)> SearchAsync(string query, int page, TrackIdentity track, CancellationToken token);
    Task<bool> ApplyAsync(QQSongCandidate candidate, long generation);
}
public sealed class RematchBackend(TrackCoordinator coordinator) : IRematchBackend
{
    public event Action? StateChanged { add => coordinator.StateChanged += value; remove => coordinator.StateChanged -= value; }
    public (TrackIdentity? Track, long Generation) Current { get { var s = coordinator.GetUiState(); return (s.Track, s.Generation); } }
    public Task<(IReadOnlyList<QQSongCandidate> Items, bool HasMore, string? Error)> SearchAsync(string query, int page, TrackIdentity track, CancellationToken token) => coordinator.ManualSearchAsync(query, page, QQMusicSearchService.DefaultManualPageSize, track, token);
    public Task<bool> ApplyAsync(QQSongCandidate candidate, long generation) => coordinator.ApplyManualMatchAsync(candidate, generation);
}
public sealed record CandidateRow(QQSongCandidate Candidate)
{
    public string Title => string.IsNullOrWhiteSpace(Candidate.Title) ? "未提供歌名" : Candidate.Title;
    public string Subtitle => string.IsNullOrWhiteSpace(Candidate.Artists) && string.IsNullOrWhiteSpace(Candidate.Album) ? "歌手和专辑信息暂缺" : string.Join(" · ", new[] { Candidate.Artists, Candidate.Album }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string Meta => (Candidate.DurationSeconds > 0 ? PlaybackPresentation.FormatTime(TimeSpan.FromSeconds(Candidate.DurationSeconds)) : "时长未知") + " · " + (Candidate.VersionFlags == SongVersionFlags.None ? "标准版本" : SongVersionDetector.Describe(Candidate.VersionFlags));
    public string Score => $"参考分 {Candidate.MatchScore:0.#}";
    public string Confidence => Candidate.Confidence switch { MatchConfidence.High => "推荐", MatchConfidence.Medium => "核对版本", _ => "低匹配" };
    public string Details => $"SongMid: {Candidate.SongMid} · SongId: {Candidate.SongId}\n{Candidate.RejectReason}";
}
public sealed class RematchViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IRematchBackend _backend;
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _queryCts;
    private TrackIdentity? _track;
    private long _generation, _request;
    private bool _closed, _searching, _applying, _stale, _hasMore;
    private int _page;
    private string _query = "", _lastQuery = "", _status = "", _error = "";
    private CandidateRow? _selected;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? Applied;
    public ObservableCollection<CandidateRow> Candidates { get; } = new();
    public RematchViewModel(IRematchBackend backend, Dispatcher? dispatcher = null)
    {
        _backend = backend; _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        backend.StateChanged += TrackChanged; CaptureTrack();
        SearchCommand = new RelayCommand(async _ => await SearchAsync(true), _ => HasTrack && !_applying && !_stale);
        MoreCommand = new RelayCommand(async _ => await SearchAsync(false), _ => _hasMore && !IsBusy && !_stale);
        RestartCommand = new RelayCommand(async _ => { CaptureTrack(); await SearchAsync(true); }, _ => !_applying);
        ApplyCommand = new RelayCommand(async _ => await ApplyAsync(), _ => CanApply);
    }
    public ICommand SearchCommand { get; }
    public ICommand MoreCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand ApplyCommand { get; }
    public bool HasTrack => !string.IsNullOrWhiteSpace(_track?.Title);
    public bool IsBusy => _searching || _applying;
    public bool IsApplying => _applying;
    public bool IsStale => _stale;
    public bool HasError => _error.Length > 0;
    public bool IsEmpty => Candidates.Count == 0 && !IsBusy;
    public string Error => _error;
    public string Status => _status;
    public string Title => _track?.Title ?? "当前没有可匹配的歌曲";
    public string Subtitle => _track is null ? "请在 QQ 音乐播放一首歌，然后匹配当前歌曲。" : $"{_track.Artist} · {PlaybackPresentation.FormatTime(_track.Duration ?? TimeSpan.Zero)}";
    public string SearchLabel => _searching ? "重新搜索" : "搜索";
    public string ApplyLabel => _applying ? "应用中…" : "应用";
    public string Query { get => _query; set { _query = value; Raise(); } }
    public CandidateRow? Selected { get => _selected; set { _selected = value; Raise(); Raise(nameof(CanApply)); CommandManager.InvalidateRequerySuggested(); } }
    public bool CanApply => Selected is not null && !IsBusy && !_stale && HasTrack;
    private void CaptureTrack()
    {
        _request++; _queryCts?.Cancel();
        (_track, _generation) = _backend.Current;
        _stale = false; _searching = false; _hasMore = false; _error = ""; _page = 0; Candidates.Clear(); Selected = null;
        Query = _track is null ? "" : (_track.Title + " " + _track.Artist).Trim();
        _status = HasTrack ? "" : "暂无播放";
        RaiseAll();
    }
    private bool IsCurrent()
    {
        var current = _backend.Current;
        return current.Generation == _generation && _track is not null && _track.SameAs(current.Track);
    }
    private void TrackChanged()
    {
        void Check() { if (!_closed && !_applying && !IsCurrent()) MarkStale(); }
        if (_dispatcher.CheckAccess()) Check(); else if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(Check);
    }
    private void MarkStale()
    {
        _stale = true; _request++; _queryCts?.Cancel(); _searching = false; _hasMore = false;
        _status = "歌曲已切换"; _error = ""; RaiseAll();
    }
    public async Task SearchAsync(bool reset)
    {
        if (_closed || _applying || !HasTrack) return;
        if (!IsCurrent()) { MarkStale(); return; }
        if (!reset && (!_hasMore || _searching)) return;
        var query = reset ? Query.Trim() : _lastQuery;
        if (query.Length == 0) { _error = "输入歌名或歌手"; RaiseAll(); return; }
        _queryCts?.Cancel(); _queryCts?.Dispose(); _queryCts = new();
        var token = _queryCts.Token; var request = ++_request;
        var page = reset ? 1 : _page + 1;
        if (reset) { Candidates.Clear(); Selected = null; _hasMore = false; _lastQuery = query; }
        _searching = true; _error = ""; _status = reset ? "搜索中…" : "加载中…"; RaiseAll();
        try
        {
            var result = await _backend.SearchAsync(query, page, _track!, token);
            if (_closed || token.IsCancellationRequested || request != _request) return;
            if (!IsCurrent()) { MarkStale(); return; }
            if (result.Error is not null && result.Items.Count == 0) { _error = "搜索失败：" + result.Error; _status = "请重试"; return; }
            var seen = Candidates.Select(c => c.Candidate.DedupKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var c in result.Items) if (Candidates.Count < QQMusicSearchService.MaxManualResults && seen.Add(c.DedupKey)) Candidates.Add(new(c));
            _page = page; _hasMore = result.HasMore && result.Items.Count > 0 && Candidates.Count < QQMusicSearchService.MaxManualResults;
            _status = Candidates.Count == 0 ? "没有搜索结果" : $"{Candidates.Count} 个版本";
            if (result.Error is not null) _error = "部分结果获取失败：" + result.Error;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (request == _request && !_closed) { _error = "搜索失败：" + ex.Message; _status = "请重试。"; } }
        finally { if (request == _request && !_closed) { _searching = false; RaiseAll(); } }
    }
    public async Task ApplyAsync()
    {
        if (!CanApply || Selected is null) return;
        if (!IsCurrent()) { MarkStale(); return; }
        _applying = true; _error = ""; _status = "加载歌词中…"; RaiseAll();
        try
        {
            var applied = await _backend.ApplyAsync(Selected.Candidate, _generation);
            if (_closed) return;
            if (!applied || !_track!.SameAs(_backend.Current.Track)) { MarkStale(); return; }
            Applied?.Invoke();
        }
        catch (Exception ex) { if (!_closed) { _error = "应用失败：" + ex.Message; _status = "请重试或选择其他版本"; } }
        finally { _applying = false; if (!_closed) RaiseAll(); }
    }
    private void RaiseAll() { Raise(""); CommandManager.InvalidateRequerySuggested(); }
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public void Dispose() { if (_closed) return; _closed = true; _request++; _queryCts?.Cancel(); _queryCts?.Dispose(); _backend.StateChanged -= TrackChanged; }
}
