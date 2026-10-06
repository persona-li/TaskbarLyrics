using System.Text.Json;
using System.Text.Json.Serialization;
using TaskbarLyrics.QQMusic.Logging;

namespace TaskbarLyrics.QQMusic.Matching;

/// <summary>
/// Optional artist name aliases for search recall only (not identity equality).
/// File: %LOCALAPPDATA%\TaskbarLyrics\Cache\artist-aliases.json
/// </summary>
public sealed class ArtistAliasStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Dictionary<string, List<string>> _map =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IQqMusicLogger? _logger;

    public ArtistAliasStore(IQqMusicLogger? logger = null, bool loadFromDisk = true)
    {
        _logger = logger;
        SeedDefaults();
        if (loadFromDisk)
        {
            LoadFromDisk();
        }
    }

    public static string DefaultFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TaskbarLyrics",
            "Cache",
            "artist-aliases.json");

    /// <summary>Aliases for this artist name, excluding the name itself.</summary>
    public IReadOnlyList<string> GetAliases(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { artist.Trim() };

        void AddFromKey(string key)
        {
            if (!_map.TryGetValue(key, out var list))
            {
                return;
            }

            foreach (var a in list)
            {
                if (string.IsNullOrWhiteSpace(a) || !seen.Add(a.Trim()))
                {
                    continue;
                }

                result.Add(a.Trim());
            }
        }

        AddFromKey(artist.Trim());

        // Reverse lookup: if artist is an alias of a canonical name, pull siblings + canonical.
        foreach (var (canonical, aliases) in _map)
        {
            if (!aliases.Any(a => string.Equals(a, artist.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (seen.Add(canonical))
            {
                result.Add(canonical);
            }

            foreach (var a in aliases)
            {
                if (string.IsNullOrWhiteSpace(a) || !seen.Add(a.Trim()))
                {
                    continue;
                }

                result.Add(a.Trim());
            }
        }

        return result;
    }

    /// <summary>True if a and b are the same artist or known aliases of each other.</summary>
    public bool AreAliases(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var aliasesA = GetAliases(a);
        if (aliasesA.Any(x => string.Equals(x, b.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var aliasesB = GetAliases(b);
        return aliasesB.Any(x => string.Equals(x, a.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private void SeedDefaults()
    {
        // General multi-language artist names — not song-specific patches.
        Put("脸红的思春期", "BOL4", "Bolbbalgan4", "볼빨간사춘기");
        Put("周杰伦", "Jay Chou", "周杰倫");
        Put("林俊杰", "JJ Lin", "林俊傑");
        Put("邓紫棋", "G.E.M.", "G.E.M.邓紫棋", "鄧紫棋");
        Put("陈奕迅", "Eason Chan", "陳奕迅");
        Put("薛之谦", "Joker Xue");
        Put("BTS", "防弹少年团", "방탄소년단", "Bangtan");
        Put("BLACKPINK", "블랙핑크");
        Put("IU", "아이유", "李知恩");
        Put("YOASOBI", "ヨアソビ");
        Put("米津玄师", "Kenshi Yonezu", "米津玄師");
        Put("NewJeans", "뉴진스");
        Put("aespa", "에스파");
        Put("SEVENTEEN", "세븐틴", "SVT");
        Put("TWICE", "트와이스");
        Put("Taylor Swift", "泰勒·斯威夫特", "泰勒斯威夫特");
        Put("The Weeknd", "威肯");
        Put("Ariana Grande", "爱莉安娜·格兰德");
    }

    private void Put(string canonical, params string[] aliases)
    {
        if (!_map.TryGetValue(canonical, out var list))
        {
            list = new List<string>();
            _map[canonical] = list;
        }

        foreach (var a in aliases)
        {
            if (!list.Any(x => string.Equals(x, a, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(a);
            }
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            var path = DefaultFilePath;
            if (!File.Exists(path))
            {
                // Write defaults so users can extend without guessing format.
                TrySaveDefaults(path);
                return;
            }

            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<AliasFileDto>(json, JsonOptions);
            if (dto?.Artists is null)
            {
                return;
            }

            foreach (var (key, aliases) in dto.Artists)
            {
                if (string.IsNullOrWhiteSpace(key) || aliases is null)
                {
                    continue;
                }

                Put(key.Trim(), aliases.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToArray());
            }

            _logger?.Info($"ArtistAliasStore loaded: {_map.Count} keys from {path}");
        }
        catch (Exception ex)
        {
            _logger?.Warn($"ArtistAliasStore load failed: {ex.Message}");
        }
    }

    private void TrySaveDefaults(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var dto = new AliasFileDto
            {
                Version = 1,
                Artists = _map.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.ToList(),
                    StringComparer.OrdinalIgnoreCase)
            };
            File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonOptions));
            _logger?.Info($"ArtistAliasStore wrote defaults: {path}");
        }
        catch (Exception ex)
        {
            _logger?.Warn($"ArtistAliasStore save defaults failed: {ex.Message}");
        }
    }

    private sealed class AliasFileDto
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, List<string>>? Artists { get; set; }
    }
}
