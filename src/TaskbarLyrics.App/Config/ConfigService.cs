using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using TaskbarLyrics.Cache;

namespace TaskbarLyrics.App.Config;

/// <summary>
/// Loads / validates / hot-reloads %LOCALAPPDATA%\TaskbarLyrics\config.json
/// </summary>
public sealed class ConfigService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Action<string> _logInfo;
    private readonly Action<string> _logWarn;
    private readonly Action<string, Exception> _logError;
    private readonly object _lock = new();
    private readonly string _configRoot;
    private readonly Func<string, bool> _fontExists;
    private long _revision;
    public ConfigSaveState SaveState { get; private set; } = new(0, SavePhase.Saved);
    public event Action<ConfigSaveState>? SaveStateChanged;
    private AppConfig _config;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounceCts;
    private CancellationTokenSource? _saveDebounceCts;
    private bool _disposed;
    private int _suppressWatcher;
    private string _lastWrittenHash = string.Empty;

    public event Action<AppConfig>? ConfigChanged;

    public ConfigService(
        Action<string> logInfo,
        Action<string> logWarn,
        Action<string, Exception> logError,
        string? configRoot = null, bool watchFile = true, Func<string, bool>? fontExists = null)
    {
        _logInfo = logInfo;
        _logWarn = logWarn;
        _logError = logError;
        _configRoot = Path.GetFullPath(configRoot ?? CachePaths.AppRoot);
        _fontExists = fontExists ?? FontExists;
        Directory.CreateDirectory(_configRoot);
        _config = LoadOrCreate();
        if (watchFile) StartWatcher();
    }

    public string ConfigPath =>
        Path.Combine(_configRoot, "config.json");

    public AppConfig Current
    {
        get
        {
            lock (_lock)
            {
                return _config;
            }
        }
    }

    public AppConfig LoadOrCreate()
    {
        var path = ConfigPath;
        try
        {
            if (!File.Exists(path))
            {
                var defaults = AppConfig.CreateDefault();
                ValidateAndNormalize(defaults, logFixes: false);
                WriteAtomic(path, defaults);
                _logInfo($"Config created: {path}");
                return defaults;
            }

            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (cfg is null)
            {
                throw new InvalidDataException("config.json deserialized to null");
            }

            ValidateAndNormalize(cfg, logFixes: true);
            // Persist clamps / one-time width migration so disk matches memory
            try
            {
                WriteAtomic(path, cfg);
            }
            catch (Exception writeEx)
            {
                _logError("Persist config after load", writeEx);
                PublishSaveState(new(_revision, SavePhase.Failed, writeEx.Message));
            }

            _logInfo($"Config loaded: {path}");
            return cfg;
        }
        catch (Exception ex)
        {
            _logWarn($"CONFIG INVALID: {ex.Message} — using defaults");
            try
            {
                if (File.Exists(path))
                {
                    var backup = Path.Combine(
                        _configRoot,
                        $"config.invalid-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                    File.Copy(path, backup, overwrite: true);
                    _logWarn($"Invalid config backed up to: {backup}");
                }
            }
            catch (Exception backupEx)
            {
                _logError("Config backup", backupEx);
            }

            var defaults = AppConfig.CreateDefault();
            ValidateAndNormalize(defaults, logFixes: false);
            try
            {
                WriteAtomic(path, defaults);
            }
            catch (Exception writeEx)
            {
                _logError("Write default config", writeEx);
                PublishSaveState(new(_revision, SavePhase.Failed, writeEx.Message));
            }

            return defaults;
        }
    }

    public void ValidateAndNormalize(AppConfig cfg, bool logFixes)
    {
        cfg.Display ??= new DisplayConfig();
        cfg.Display.PaletteHue = double.IsFinite(cfg.Display.PaletteHue)
            ? Math.Clamp(cfg.Display.PaletteHue, 0, 360) : OklchLyricPalette.DefaultHue;
        cfg.Display.Fonts ??= new FontConfig();
        cfg.Overlay ??= new OverlayConfig();
        cfg.Karaoke ??= new KaraokeConfig();
        cfg.Lyrics ??= new LyricsUserConfig();
        cfg.General ??= new GeneralConfig();
        cfg.General.RecentColors = RecentColorHistory.Normalize(cfg.General.RecentColors);
        cfg.General.RecentNormalColors = RecentColorHistory.Normalize(cfg.General.RecentNormalColors);
        cfg.General.RecentHighlightColors = RecentColorHistory.Normalize(cfg.General.RecentHighlightColors);
        cfg.General.RecentShadowColors = RecentColorHistory.Normalize(cfg.General.RecentShadowColors);

        // Retired experimental theme: keep the system-selected light/dark appearance.
        if (cfg.General.Theme == "Glass") cfg.General.Theme = "FollowSystem";
        if (cfg.General.Theme is not ("Dark" or "Light" or "FollowSystem"))
        {
            cfg.General.Theme = "Dark";
            if (logFixes)
            {
                _logWarn("Unknown theme; using Dark");
            }
        }

        cfg.Lyrics.GlobalOffsetMs = (long)Clamp(cfg.Lyrics.GlobalOffsetMs, -5000, 5000, "globalOffsetMs", logFixes);

        cfg.Display.FontSize = Clamp(cfg.Display.FontSize, 10, 72, "fontSize", logFixes);
        cfg.Display.VerticalOffsetPx = Clamp(cfg.Display.VerticalOffsetPx, -10, 10, "verticalOffsetPx", logFixes);
        cfg.Display.ShadowOffsetX = Clamp(cfg.Display.ShadowOffsetX, -5, 5, "shadowOffsetX", logFixes);
        cfg.Display.ShadowOffsetY = Clamp(cfg.Display.ShadowOffsetY, -5, 5, "shadowOffsetY", logFixes);

        // One-time widen: old defaults (0.40 / 500 / 900) → ~3/2 (0.60 / 750 / 1350)
        if (Math.Abs(cfg.Overlay.WidthRatio - 0.40) < 0.001
            && cfg.Overlay.MinWidthPx == 500
            && cfg.Overlay.MaxWidthPx == 900)
        {
            cfg.Overlay.WidthRatio = 0.60;
            cfg.Overlay.MinWidthPx = 750;
            cfg.Overlay.MaxWidthPx = 1350;
            if (logFixes)
            {
                _logInfo("Overlay width defaults migrated to ~1.5× (ratio 0.60, min 750, max 1350)");
            }
        }

        cfg.Overlay.WidthRatio = Clamp(cfg.Overlay.WidthRatio, 0.20, OverlayConfig.MaxTaskbarWidthFraction, "widthRatio", logFixes);
        cfg.Overlay.MinWidthPx = (int)Clamp(cfg.Overlay.MinWidthPx, 200, 1800, "minWidthPx", logFixes);
        cfg.Overlay.MaxWidthPx = (int)Clamp(cfg.Overlay.MaxWidthPx, 300, 2400, "maxWidthPx", logFixes);
        cfg.Overlay.LeftMarginPx = (int)Clamp(cfg.Overlay.LeftMarginPx, 0, 80, "leftMarginPx", logFixes);
        cfg.Overlay.RightSafetyMarginPx = (int)Clamp(cfg.Overlay.RightSafetyMarginPx, 0, 200, "rightSafetyMarginPx", logFixes);

        if (cfg.Overlay.MinWidthPx > cfg.Overlay.MaxWidthPx)
        {
            if (logFixes)
            {
                _logWarn($"minWidthPx > maxWidthPx; swapping ({cfg.Overlay.MinWidthPx}, {cfg.Overlay.MaxWidthPx})");
            }

            (cfg.Overlay.MinWidthPx, cfg.Overlay.MaxWidthPx) = (cfg.Overlay.MaxWidthPx, cfg.Overlay.MinWidthPx);
        }

        cfg.Karaoke.RenderHz = (int)Clamp(cfg.Karaoke.RenderHz, 15, 60, "renderHz", logFixes);
        cfg.Karaoke.AutoPanRightPaddingPx = (int)Clamp(cfg.Karaoke.AutoPanRightPaddingPx, 20, 200, "autoPanRightPaddingPx", logFixes);

        cfg.Display.ParsedNormalColor = ParseColor(cfg.Display.NormalColor, Color.FromRgb(0x49, 0x6D, 0xBF), "normalColor", logFixes);
        cfg.Display.ParsedHighlightColor = ParseColor(cfg.Display.HighlightColor, Color.FromRgb(0xA0, 0xCC, 0xEE), "highlightColor", logFixes);
        cfg.Display.ParsedShadowColor = ParseColor(cfg.Display.ShadowColor, Color.FromArgb(0x80, 0x00, 0x00, 0x00), "shadowColor", logFixes);

        cfg.Display.TextAlignment = cfg.Display.TextAlignment?.ToUpperInvariant() switch
        {
            "LEFT" => "Left",
            "RIGHT" => "Right",
            _ => "Center"
        };
        cfg.Display.AlignCenter = cfg.Display.TextAlignment == "Center";

        cfg.Display.Fonts.Chinese = ResolveFont(cfg.Display.Fonts.Chinese, FontConfig.DefaultFamily, "chinese", logFixes);
        cfg.Display.Fonts.Japanese = ResolveFont(cfg.Display.Fonts.Japanese, FontConfig.DefaultFamily, "japanese", logFixes);
        cfg.Display.Fonts.Korean = ResolveFont(cfg.Display.Fonts.Korean, FontConfig.DefaultFamily, "korean", logFixes);
        cfg.Display.Fonts.Latin = ResolveFont(cfg.Display.Fonts.Latin, FontConfig.DefaultFamily, "latin", logFixes);
        cfg.Display.Fonts.Cyrillic = ResolveFont(cfg.Display.Fonts.Cyrillic, FontConfig.DefaultFamily, "cyrillic", logFixes);
        cfg.Display.Fonts.Arabic = ResolveFont(cfg.Display.Fonts.Arabic, FontConfig.DefaultFamily, "arabic", logFixes);
        cfg.Display.Fonts.Other = ResolveFont(cfg.Display.Fonts.Other, FontConfig.DefaultFamily, "other", logFixes);
    }

    /// <summary>
    /// Overlay width in physical pixels.
    /// Preferred: free band from taskbar left to Start button left (caller supplies availableWidth).
    /// Fallback: Clamp(taskbar.Width * ratio, min, max) with hard safety fraction.
    /// </summary>
    public static int ComputeOverlayWidthPx(
        int taskbarWidthPx,
        OverlayConfig o,
        int? freeBandWidthPx = null)
    {
        if (taskbarWidthPx <= 0)
        {
            return Math.Max(1, o.MinWidthPx);
        }

        int width;
        if (freeBandWidthPx is int free && free >= 80)
        {
            // Primary: measured gap [taskbar.Left → Start.Left]
            width = free;
        }
        else
        {
            // Fallback: ratio of full taskbar
            width = (int)Math.Round(taskbarWidthPx * o.WidthRatio);
        }

        width = Math.Clamp(width, o.MinWidthPx, o.MaxWidthPx);

        var hardCap = (int)Math.Floor(taskbarWidthPx * OverlayConfig.MaxTaskbarWidthFraction);
        if (hardCap > 0)
        {
            width = Math.Min(width, hardCap);
        }

        width = Math.Min(width, Math.Max(1, taskbarWidthPx - o.LeftMarginPx - o.RightSafetyMarginPx));
        return Math.Max(1, width);
    }

    private void StartWatcher()
    {
        try
        {
            _watcher = new FileSystemWatcher(_configRoot, "config.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };
            _watcher.Changed += OnConfigFileChanged;
            _watcher.Created += OnConfigFileChanged;
            _watcher.Renamed += OnConfigFileChanged;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            _logError("Config FileSystemWatcher", ex);
        }
    }

    /// <summary>
    /// Mutate in-memory config, notify listeners immediately, debounce disk save.
    /// </summary>
    public void Update(Action<AppConfig> mutator, bool notify = true, bool scheduleSave = true)
    {
        AppConfig snapshot;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _config = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(_config, JsonOptions), JsonOptions)!;
            mutator(_config);
            ValidateAndNormalize(_config, logFixes: true);
            _revision++;
            snapshot = _config;
        }

        if (notify)
        {
            ConfigChanged?.Invoke(snapshot);
        }

        if (scheduleSave)
        {
            ScheduleSave();
        }
    }

    public void ResetColorsToDefaults() => Update(LyricColorScheme.Default.Apply);

    public void ResetAppearanceToDefaults()
    {
        Update(cfg =>
        {
            cfg.Display = new DisplayConfig();
            cfg.Overlay = new OverlayConfig();
            cfg.Karaoke = new KaraokeConfig();
        });
    }

    public void ScheduleSave()
    {
        CancellationToken token;
        long revision;
        lock (_lock)
        {
            if (_disposed) return;
            _saveDebounceCts?.Cancel();
            _saveDebounceCts?.Dispose();
            _saveDebounceCts = new CancellationTokenSource();
            token = _saveDebounceCts.Token;
            revision = _revision;
        }
        PublishSaveState(new(revision, SavePhase.Pending));
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400, token).ConfigureAwait(false);
                lock (_lock)
                {
                    if (_disposed || revision != _revision || token.IsCancellationRequested) return;
                    SaveNow();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logError("Config save", ex); }
        });
    }

    // Serialize and write under the revision lock: an older snapshot must never win.
    public void SaveNow()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _saveDebounceCts?.Cancel();
            var revision = _revision;
            PublishSaveState(new(revision, SavePhase.Saving));
            try
            {
                Interlocked.Exchange(ref _suppressWatcher, 1);
                WriteAtomic(ConfigPath, _config);
                PublishSaveState(new(revision, SavePhase.Saved));
                _logInfo("Config saved");
            }
            catch (Exception ex)
            {
                PublishSaveState(new(revision, SavePhase.Failed, ex.Message));
                throw;
            }
            finally { Interlocked.Exchange(ref _suppressWatcher, 0); }
        }
    }

    private void PublishSaveState(ConfigSaveState state)
    {
        lock (_lock)
        {
            if (state.Revision != _revision) return;
            SaveState = state;
        }
        SaveStateChanged?.Invoke(state);
    }

    private void OnConfigFileChanged(object sender, FileSystemEventArgs e)
    {
        if (Interlocked.CompareExchange(ref _suppressWatcher, 0, 0) != 0)
        {
            return;
        }

        try
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(600, token).ConfigureAwait(false);
                    ReloadFromDisk();
                }
                catch (OperationCanceledException)
                {
                    // debounce
                }
                catch (Exception ex)
                {
                    _logError("Config hot reload", ex);
                }
            }, token);
        }
        catch (Exception ex)
        {
            _logError("Config watcher handler", ex);
        }
    }

    private void ReloadFromDisk()
    {
        if (Interlocked.CompareExchange(ref _suppressWatcher, 0, 0) != 0)
        {
            return;
        }

        var path = ConfigPath;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(path);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(json)));
            if (hash == _lastWrittenHash)
            {
                return;
            }

            var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (cfg is null)
            {
                _logWarn("Hot reload: config null — keep previous");
                return;
            }

            ValidateAndNormalize(cfg, logFixes: true);
            lock (_lock)
            {
                _saveDebounceCts?.Cancel();
                _config = cfg;
                _revision++;
                PublishSaveState(new(_revision, SavePhase.Saved));
            }

            _logInfo("Config hot-reloaded successfully");
            ConfigChanged?.Invoke(cfg);
        }
        catch (Exception ex)
        {
            // Runtime invalid edit: keep previous, do not overwrite user file
            _logWarn($"Hot reload failed (keeping previous config): {ex.Message}");
        }
    }

    private void WriteAtomic(string path, AppConfig cfg)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(cfg, JsonOptions);
        var writtenHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(json)));
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        if (File.Exists(path))
        {
            File.Replace(temp, path, null, true);
        }
        else
        {
            File.Move(temp, path);
        }
        _lastWrittenHash = writtenHash;
    }

    private Color ParseColor(string? text, Color fallback, string name, bool log)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        try
        {
            var c = (Color)ColorConverter.ConvertFromString(text.Trim())!;
            return c;
        }
        catch
        {
            if (log)
            {
                _logWarn($"Invalid color '{text}' for {name}; using default");
            }

            return fallback;
        }
    }

    private string ResolveFont(string? requested, string preferredFallback, string name, bool log)
    {
        var candidates = new[]
        {
            requested ?? string.Empty,
            preferredFallback,
            "Segoe UI"
        };

        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c))
            {
                continue;
            }

            if (_fontExists(c.Trim()))
            {
                if (log && !string.Equals(c.Trim(), requested?.Trim(), StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(requested))
                {
                    _logWarn($"Font '{requested}' for {name} not found; fallback '{c.Trim()}'");
                }

                return c.Trim();
            }
        }

        return "Segoe UI";
    }

    private static bool FontExists(string familyName)
    {
        try
        {
            var ff = new FontFamily(familyName);
            // Touch typefaces to force resolution
            foreach (var _ in ff.GetTypefaces())
            {
                return true;
            }

            // Some families report empty typefaces but still work
            return !string.IsNullOrWhiteSpace(ff.Source);
        }
        catch
        {
            return false;
        }
    }

    private double Clamp(double value, double min, double max, string name, bool log)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            if (log)
            {
                _logWarn($"{name} invalid; using {min}");
            }

            return min;
        }

        var clamped = Math.Clamp(value, min, max);
        if (log && Math.Abs(clamped - value) > 0.0001)
        {
            _logWarn($"{name}={value} clamped to {clamped}");
        }

        return clamped;
    }

    public void Dispose() => DisposeCore(flushPending: true);

    public void DisposeWithoutSaving() => DisposeCore(flushPending: false);

    private void DisposeCore(bool flushPending)
    {
        if (_disposed)
        {
            return;
        }

        lock (_lock)
        {
            _saveDebounceCts?.Cancel();
            if (flushPending && SaveState.Phase is (SavePhase.Pending or SavePhase.Failed))
            {
                try { SaveNow(); } catch (Exception ex) { _logError("Final config save", ex); }
            }
            _disposed = true;
            _saveDebounceCts?.Dispose();
        }
        try
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
        }
        catch
        {
            // ignore
        }

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
    }
}
