#include "lyrics_library.hpp"
#include "lyrics_match.hpp"
#include "lyrics_qrc.hpp"
#include "platform.hpp"
#include <cwctype>
#include <future>
#include <optional>
#include <winhttp.h>
namespace lyrics
{
namespace
{
std::mutex persistenceMutex;
thread_local std::function<std::string(const std::wstring &)> testTransport;
std::map<std::string, std::shared_ptr<std::mutex>> songLocks;
std::shared_ptr<std::mutex> songMutex(const std::string &key)
{
    std::lock_guard lock(persistenceMutex);
    auto &p = songLocks[key];
    if (!p)
        p = std::make_shared<std::mutex>();
    return p;
}
struct MissingLyrics : std::runtime_error
{
    MissingLyrics() : std::runtime_error("这首歌暂无可用歌词")
    {
    }
};
std::string normalized(const std::string &text)
{
    auto s = wide(text);
    std::wstring out;
    bool space = false;
    for (auto c : s)
    {
        if (iswspace(c))
        {
            space = !out.empty();
            continue;
        }
        if (space)
        {
            out += L' ';
            space = false;
        }
        out += static_cast<wchar_t>(towlower(c));
    }
    return utf8(lowerInvariant(out));
}
std::string encode(const std::string &s)
{
    std::ostringstream out;
    out << std::uppercase << std::hex;
    for (unsigned char c : s)
        if (std::isalnum(c) || c == '-' || c == '_' || c == '.' || c == '~')
            out << c;
        else
            out << '%' << std::setw(2) << std::setfill('0') << static_cast<int>(c);
    return out.str();
}
bool validMid(const std::string &s)
{
    return !s.empty() &&
           s.find_first_not_of("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789") == std::string::npos;
}
bool validId(const std::string &s)
{
    return !s.empty() && s.find_first_not_of("0123456789") == std::string::npos;
}
std::string cacheKey(const Json &candidate)
{
    auto mid = candidate.value("mid", "");
    if (validMid(mid))
        return "qq-" + mid;
    auto id = candidate.value("id", "");
    if (validId(id))
        return "qq-id-" + id;
    throw std::runtime_error("无效歌曲标识");
}
std::string stringValue(const Json &j, const char *key)
{
    if (!j.contains(key) || j[key].is_null())
        return {};
    return j[key].is_string() ? j[key].get<std::string>() : j[key].dump();
}
void abortIf(const std::function<bool()> &cancelled)
{
    if (cancelled && cancelled())
        throw std::runtime_error("请求已取消");
}
struct Internet
{
    HINTERNET h{};
    explicit Internet(HINTERNET p) : h(p)
    {
    }
    ~Internet()
    {
        if (h)
            WinHttpCloseHandle(h);
    }
    operator HINTERNET() const
    {
        return h;
    }
};
std::string get(const std::wstring &path, const std::function<bool()> &cancelled = {})
{
    abortIf(cancelled);
    if (testTransport)
    {
        auto body = testTransport(path);
        abortIf(cancelled);
        return body;
    }
    Internet session(WinHttpOpen(L"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
                                 L"Chrome/140.0.0.0 Safari/537.36",
                                 WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY, nullptr, nullptr, 0));
    if (!session.h)
        throw std::runtime_error("网络初始化失败");
    WinHttpSetTimeouts(session, 5000, 5000, 8000, 8000);
    Internet connection(WinHttpConnect(session, L"c.y.qq.com", INTERNET_DEFAULT_HTTPS_PORT, 0));
    if (!connection.h)
        throw std::runtime_error("网络连接失败");
    Internet request(WinHttpOpenRequest(connection, L"GET", path.c_str(), nullptr, WINHTTP_NO_REFERER,
                                        WINHTTP_DEFAULT_ACCEPT_TYPES, WINHTTP_FLAG_SECURE));
    if (!request.h ||
        !WinHttpSendRequest(request, L"Referer: https://c.y.qq.com/\r\nAccept: */*\r\nCache-Control: no-cache\r\n",
                            static_cast<DWORD>(-1), nullptr, 0, 0, 0) ||
        !WinHttpReceiveResponse(request, nullptr))
        throw std::runtime_error("歌词服务网络请求失败");
    abortIf(cancelled);
    DWORD status = 0, size = sizeof(status);
    if (!WinHttpQueryHeaders(request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                             WINHTTP_HEADER_NAME_BY_INDEX, &status, &size, WINHTTP_NO_HEADER_INDEX) ||
        status != 200)
        throw std::runtime_error("歌词服务 HTTP " + std::to_string(status));
    std::string body;
    for (;;)
    {
        abortIf(cancelled);
        DWORD available = 0;
        if (!WinHttpQueryDataAvailable(request, &available))
            throw std::runtime_error("网络读取失败");
        if (!available)
            break;
        if (body.size() + available > 8 * 1024 * 1024)
            throw std::runtime_error("响应过大");
        size_t start = body.size();
        body.resize(start + available);
        DWORD received = 0;
        if (!WinHttpReadData(request, body.data() + start, available, &received))
            throw std::runtime_error("网络读取失败");
        body.resize(start + received);
    }
    abortIf(cancelled);
    return body;
}
Json parseJsonp(std::string body)
{
    size_t first = body.find('{'), last = body.rfind('}');
    if (first == std::string::npos || last == std::string::npos)
        throw std::runtime_error("歌词服务返回格式无效");
    return Json::parse(body.substr(first, last - first + 1));
}
void writeText(const fs::path &path, const std::string &text)
{
    fs::create_directories(path.parent_path());
    auto temp = path;
    temp += L".tmp";
    {
        std::ofstream f(temp, std::ios::binary | std::ios::trunc);
        f << text;
        f.flush();
        if (!f)
            throw std::runtime_error("无法保存歌词缓存");
    }
    if (!MoveFileExW(temp.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
        throw std::runtime_error("无法替换歌词缓存");
}
std::string isoNow()
{
    SYSTEMTIME time{};
    GetSystemTime(&time);
    char value[40]{};
    sprintf_s(value, "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ", time.wYear, time.wMonth, time.wDay, time.wHour,
              time.wMinute, time.wSecond, time.wMilliseconds);
    return value;
}
Json serializeLines(const std::vector<Line> &lines)
{
    Json json = Json::array();
    for (auto &l : lines)
    {
        Json words = Json::array();
        for (auto &w : l.words)
            words.push_back({{"text", w.text}, {"startMs", w.start}, {"durationMs", w.duration}});
        json.push_back({{"text", l.text}, {"startMs", l.start}, {"durationMs", l.duration}, {"words", words}});
    }
    return json;
}
void attachTranslation(std::vector<Line> &lines, const std::string &text)
{
    auto translated = parseLrc(text);
    if (translated.empty())
        translated = qrc::parse(text);
    for (auto &l : lines)
    {
        double best = 121;
        std::string found;
        for (auto &t : translated)
        {
            double diff = std::abs(t.start - l.start);
            if (diff < best)
            {
                best = diff;
                found = t.text;
            }
        }
        if (best <= 120 && found != l.text)
            l.translation = found;
    }
}
Document fromFolder(const fs::path &folder, bool repair = false)
{
    Document doc;
    auto metadata = readJson(folder / L"metadata.json");
    if (metadata.is_object() && metadata.contains("cacheVersion") && metadata.value("cacheVersion", 1) != 1)
        return {};
    if (metadata.is_object() && metadata.value("isInstrumental", false))
        doc.instrumental = true;
    auto parsed = readJson(folder / L"parsed-qrc.json");
    if (parsed.is_array())
    {
        try
        {
            for (auto &row : parsed)
            {
                Line l{row.value("text", ""), "", row.value("startMs", 0.), row.value("durationMs", 0.), {}};
                if (row.contains("words") && row["words"].is_array())
                    for (auto &w : row["words"])
                        l.words.push_back({w.value("text", ""), w.value("startMs", 0.), w.value("durationMs", 0.)});
                if (!l.text.empty() && std::isfinite(l.start) && std::isfinite(l.duration) && l.start >= 0)
                    doc.lines.push_back(std::move(l));
            }
        }
        catch (const std::exception &)
        {
            doc.lines.clear();
        }
    }
    if (doc.lines.empty())
    {
        doc.lines =
            qrc::parse(readText(folder / L"lyrics.qrc")); // Repair old or missing parser output without network.
        if (repair && !doc.lines.empty())
            saveJson(folder / L"parsed-qrc.json", serializeLines(doc.lines));
    }
    if (doc.lines.empty())
        doc.lines = parseLrc(readText(folder / L"lyrics.lrc"));
    std::stable_sort(doc.lines.begin(), doc.lines.end(), [](auto &a, auto &b) { return a.start < b.start; });
    attachTranslation(doc.lines, readText(folder / L"translation.lrc"));
    doc.origin = "本地缓存";
    doc.cacheKey = folder.filename().string();
    if (!doc.cacheKey.starts_with("qq-"))
        doc.cacheKey = "qq-" + doc.cacheKey;
    return doc;
}
Json findEntry(const fs::path &root, const Snapshot &track, bool manualOnly, bool &ambiguous)
{
    auto index = readJson(root / L"Cache" / L"track-index.json");
    if (!index.is_object() || !index.contains("tracks") || !(index["tracks"].is_object() || index["tracks"].is_array()))
        return {};
    Json chosen;
    auto title = normalized(track.title), artist = normalized(track.artist);
    int duration = static_cast<int>(std::round(track.duration / 1000));
    for (auto &entry : index["tracks"])
    {
        if (!entry.is_object() || entry.value("titleNormalized", "") != title ||
            entry.value("artistNormalized", "") != artist)
            continue;
        bool manual = entry.value("matchSource", "") == "Manual";
        if (manualOnly != manual)
            continue;
        if (!manual)
        {
            if (!track.album.empty() && matching::albumScore(track.album, entry.value("album", "")) < 95)
                continue;
            if ((matching::versions(track.title, track.album) ^
                 matching::versions(entry.value("title", track.title), entry.value("album", ""))) &
                matching::Languages)
                continue;
            // Prefer recorded input duration; old index fixtures may only have the matched duration.
            double input = entry.contains("durationSeconds") && entry["durationSeconds"].is_number()
                               ? entry["durationSeconds"].get<double>()
                               : entry.value("matchedDurationSeconds", 0.);
            if (duration <= 0 || std::abs(input - duration) > 2)
                continue;
        }
        auto mid = entry.value("songMid", "");
        if (!validMid(mid) && !validId(entry.value("songId", "")))
            continue;
        if (!chosen.is_null() && chosen.value("songMid", "") != mid)
            ambiguous = true;
        else
            chosen = entry;
    }
    return chosen;
}
Json asCandidate(const Json &entry)
{
    return {{"id", entry.value("songId", "")},   {"mid", entry.value("songMid", "")},
            {"title", entry.value("title", "")}, {"artist", entry.value("artists", "")},
            {"album", entry.value("album", "")}, {"duration", entry.value("matchedDurationSeconds", 0)}};
}
Document candidateCache(const fs::path &data, const fs::path &legacy, const Json &song)
{
    auto key = cacheKey(song), mid = song.value("mid", "");
    auto mutex = songMutex(key);
    std::lock_guard lock(*mutex);
    std::vector<fs::path> folders{data / L"Cache" / L"Lyrics" / key, data / L"Lyrics" / mid,
                                  legacy / L"Cache" / L"Lyrics" / key};
    for (auto &folder : folders)
    {
        if (!fs::is_directory(folder))
            continue;
        auto doc = fromFolder(folder, folder == data / L"Cache" / L"Lyrics" / key);
        doc.cacheKey = key;
        if (!doc.lines.empty() || doc.instrumental)
            return doc;
    }
    return {};
}
Json searchPage(const std::string &query, int page, int count, const std::function<bool()> &cancelled = {})
{
    auto body = get(wide("/soso/fcgi-bin/"
                         "client_search_cp?ct=24&qqmusic_ver=1298&remoteplace=txt.yqq.center&t=0&aggr=1&cr=1&catZhida="
                         "1&lossless=0&flag_qc=0&p=" +
                         std::to_string(page) + "&n=" + std::to_string(count) + "&w=" + encode(query) +
                         "&g_tk=5381&loginUin=0&hostUin=0&format=json&inCharset=utf8&outCharset=utf-8&notice=0&"
                         "platform=yqq&needNewCode=0"),
                    cancelled);
    auto j = parseJsonp(body);
    if (j.value("code", -1) != 0 || !j.contains("data") || !j["data"].contains("song"))
        throw std::runtime_error("搜索服务未返回有效结果");
    Json results = Json::array();
    auto &songs = j["data"]["song"];
    if (!songs.contains("list") || !songs["list"].is_array())
        return results;
    for (auto &s : songs["list"])
    {
        std::string artist;
        if (s.contains("singer") && s["singer"].is_array())
            for (auto &a : s["singer"])
            {
                if (!artist.empty())
                    artist += " / ";
                artist += qrc::entities(a.value("name", ""));
            }
        results.push_back({{"id", stringValue(s, "songid")},
                           {"mid", s.value("songmid", "")},
                           {"title", qrc::entities(s.value("songname", ""))},
                           {"artist", artist},
                           {"album", qrc::entities(s.value("albumname", ""))},
                           {"duration", s.value("interval", 0)}});
    }
    return results;
}
Document fetchLyrics(const fs::path &root, const Json &candidate, const std::function<bool()> &cancelled = {})
{
    auto key = cacheKey(candidate), id = candidate.value("id", "");
    if (!validId(id))
        throw std::runtime_error("无效歌曲标识");
    auto mutex = songMutex(key);
    std::lock_guard lock(*mutex);
    abortIf(cancelled);
    std::string lrc, trans, roma, qrcText, lrcError, qrcError;
    bool instrumental = false, lrcOk = false, qrcOk = false;
    // Both sources are independent, and QRC translation takes precedence. A request failure must not erase a working
    // LRC.
    try
    {
        auto j = parseJsonp(get(wide("/lyric/fcgi-bin/fcg_query_lyric_new.fcg?musicid=" + id +
                                     "&g_tk=5381&loginUin=0&hostUin=0&format=json&inCharset=utf8&outCharset=utf8&"
                                     "notice=0&platform=yqq&needNewCode=0&nobase64=1"),
                                cancelled));
        int code = j.value("code", -1);
        if (code != 0 && code != -1901)
            throw std::runtime_error("LRC服务返回 " + std::to_string(code));
        lrcOk = true;
        lrc = qrc::entities(j.value("lyric", ""));
        trans = qrc::entities(j.value("trans", ""));
        instrumental = lrc.find("此歌曲为没有填词的纯音乐") != std::string::npos;
    }
    catch (const std::exception &e)
    {
        abortIf(cancelled);
        lrcError = e.what();
    }
    abortIf(cancelled);
    try
    {
        auto body =
            get(wide("/qqmusic/fcgi-bin/lyric_download.fcg?musicid=" + id + "&version=15&miniversion=82&lrctype=4"),
                cancelled);
        if (body.find('<') == std::string::npos)
            throw std::runtime_error("QRC服务返回格式无效");
        auto original = qrc::node(body, "content");
        if (original.empty())
            original = qrc::extract(body);
        if (!original.empty())
            qrcText = qrc::decrypt(original);
        auto translation = qrc::node(body, "contentts");
        if (!translation.empty())
        {
            try
            {
                auto value = qrc::decrypt(translation);
                if (!value.empty())
                    trans = value;
            }
            catch (const std::exception &)
            {
            }
        }
        auto roman = qrc::node(body, "contentroma");
        if (!roman.empty())
        {
            try
            {
                roma = qrc::decrypt(roman);
            }
            catch (const std::exception &)
            {
            }
        }
        qrcOk = true;
    }
    catch (const std::exception &e)
    {
        abortIf(cancelled);
        qrcError = e.what();
    }
    auto lines = qrc::parse(qrcText);
    bool hasQrc = !lines.empty();
    if (lines.empty() && !instrumental)
        lines = parseLrc(lrc);
    if (lines.empty() && !instrumental)
    {
        if (lrcOk && qrcOk)
            throw MissingLyrics();
        throw std::runtime_error("歌词下载失败：" + (!lrcError.empty() ? lrcError : qrcError));
    }
    abortIf(cancelled);
    auto folder = root / L"Cache" / L"Lyrics" / key;
    writeText(folder / L"lyrics.lrc", lrc);
    writeText(folder / L"lyrics.qrc", qrcText);
    writeText(folder / L"translation.lrc", trans);
    writeText(folder / L"romanization.lrc", roma);
    if (hasQrc)
        saveJson(folder / L"parsed-qrc.json", serializeLines(lines));
    else if (fs::exists(folder / L"parsed-qrc.json"))
        fs::remove(folder / L"parsed-qrc.json");
    Json metadata = {{"cacheVersion", 1},
                     {"parserVersion", 1},
                     {"provider", "QQMusic"},
                     {"songId", id},
                     {"songMid", candidate.value("mid", "")},
                     {"title", candidate.value("title", "")},
                     {"artists", Json::array({candidate.value("artist", "")})},
                     {"album", candidate.value("album", "")},
                     {"durationMs", candidate.value("duration", 0) * 1000},
                     {"hasLrc", !lrc.empty()},
                     {"hasQrc", hasQrc},
                     {"hasTranslation", !trans.empty()},
                     {"hasRomanization", !roma.empty()},
                     {"cachedAt", isoNow()},
                     {"isInstrumental", instrumental},
                     {"userModified", false}};
    saveJson(folder / L"metadata.json", metadata);
    attachTranslation(lines, trans);
    return {std::move(lines), "在线歌词", "", key, instrumental};
}
void saveAutomatic(const fs::path &root, const Snapshot &track, const matching::Candidate &candidate,
                   const std::function<bool()> &cancelled)
{
    std::lock_guard lock(persistenceMutex);
    abortIf(cancelled);
    auto path = root / L"Cache" / L"track-index.json";
    auto index = readJson(path);
    if (!index.is_object())
        index = Json::object();
    if (!index.contains("tracks") || !index["tracks"].is_object())
        index["tracks"] = Json::object();
    index["version"] = 1;
    auto title = normalized(track.title), artist = normalized(track.artist);
    int seconds = static_cast<int>(std::round(track.duration / 1000));
    for (auto &entry : index["tracks"])
        if (entry.value("titleNormalized", "") == title && entry.value("artistNormalized", "") == artist &&
            entry.value("matchSource", "") == "Manual")
            return;
    auto key = title + "|" + artist + "|" + (seconds > 0 ? std::to_string(seconds) : "na");
    auto &song = candidate.song;
    index["tracks"][key] = {{"titleNormalized", title},
                            {"artistNormalized", artist},
                            {"durationSeconds", seconds > 0 ? Json(seconds) : Json(nullptr)},
                            {"songMid", song.value("mid", "")},
                            {"songId", song.value("id", "")},
                            {"title", song.value("title", "")},
                            {"artists", song.value("artist", "")},
                            {"album", song.value("album", "")},
                            {"matchedDurationSeconds", song.value("duration", 0)},
                            {"matchSource", "Automatic"},
                            {"lastUsedAt", isoNow()},
                            {"matchScore", candidate.score}};
    abortIf(cancelled);
    saveJson(path, index);
}
} // namespace
ScopedLibraryTransport::ScopedLibraryTransport(std::function<std::string(const std::wstring &)> request)
    : previous(std::move(testTransport))
{
    testTransport = std::move(request);
}
ScopedLibraryTransport::~ScopedLibraryTransport()
{
    testTransport = std::move(previous);
}
Document Library::load(const Snapshot &track, const Json &settings)
{
    try
    {
        if (settings.contains("manual") && settings["manual"].contains(track.key()))
        {
            auto song = settings["manual"][track.key()];
            auto doc = candidateCache(data, legacy, song);
            if (!doc.lines.empty() || doc.instrumental)
            {
                doc.origin = "手动匹配";
                return doc;
            }
            // Do not fall through to an automatic binding when the chosen manual version has a missing cache.
            return {};
        }
        auto imported = importedLyricsPath(data, track.key());
        if (fs::exists(imported))
            return {parseLrc(readText(imported)), "手动导入", "", "import-" + imported.stem().string(), false};
        bool ambiguous = false;
        auto manual = findEntry(data, track, true, ambiguous);
        if (manual.is_null() && legacy != data)
            manual = findEntry(legacy, track, true, ambiguous);
        if (!manual.is_null())
        {
            auto doc = candidateCache(data, legacy, asCandidate(manual));
            doc.origin = legacy != data ? "原版手动匹配" : "手动匹配";
            return doc;
        }
        auto automatic = findEntry(data, track, false, ambiguous);
        if (automatic.is_null() && legacy != data)
            automatic = findEntry(legacy, track, false, ambiguous);
        if (ambiguous)
            return {{}, "", "缓存中有多个版本，请手动选择"};
        if (automatic.is_null())
            return {};
        auto doc = candidateCache(data, legacy, asCandidate(automatic));
        doc.origin = legacy != data ? "原版缓存" : "本地缓存";
        return doc;
    }
    catch (const std::exception &e)
    {
        return {{}, "", e.what()};
    }
}
Document Library::resolve(const Snapshot &track, const Json &settings, std::function<bool()> cancelled)
{
    abortIf(cancelled);
    auto cached = load(track, settings);
    if (!cached.lines.empty() || cached.instrumental)
        return cached;
    if (settings.contains("manual") && settings["manual"].contains(track.key()))
        return fetchLyrics(data, settings["manual"][track.key()], cancelled);
    bool ambiguous = false;
    auto manual = findEntry(data, track, true, ambiguous);
    if (manual.is_null() && legacy != data)
        manual = findEntry(legacy, track, true, ambiguous);
    if (!manual.is_null())
        return fetchLyrics(data, asCandidate(manual), cancelled);
    if (!settings.value("autoMatch", true) || track.title.empty())
        return cached;
    auto automatic = findEntry(data, track, false, ambiguous);
    if (automatic.is_null() && legacy != data)
        automatic = findEntry(legacy, track, false, ambiguous);
    std::string failedMid;
    if (!automatic.is_null() && !ambiguous && validId(automatic.value("songId", "")))
    {
        try
        {
            return fetchLyrics(data, asCandidate(automatic), cancelled);
        }
        catch (const MissingLyrics &)
        {
            abortIf(cancelled);
            failedMid = automatic.value("songMid", "");
        }
    }
    matching::Aliases aliases;
    aliases.load(readJson(data / L"Cache" / L"artist-aliases.json"));
    if (legacy != data)
        aliases.load(readJson(legacy / L"Cache" / L"artist-aliases.json"));
    Json merged = Json::array();
    std::set<std::string> seen, missing;
    std::string networkError;
    if (!failedMid.empty())
        missing.insert(failedMid);
    auto candidateKey = [](const Json &song) {
        auto mid = song.value("mid", "");
        return mid.empty() ? "id:" + song.value("id", "") : mid;
    };
    auto merge = [&](const Json &items) {
        for (auto &song : items)
        {
            auto key = candidateKey(song);
            if (!key.empty() && !missing.contains(key) && seen.insert(key).second && merged.size() < 60)
                merged.push_back(song);
        }
    };
    auto decide = [&]() -> std::optional<Document> {
        auto ranked = matching::rank(track, merged, aliases);
        if (!matching::canBind(ranked, track))
            return {};
        try
        {
            auto doc = candidateCache(data, legacy, ranked.front().song);
            if (doc.lines.empty() && !doc.instrumental)
                doc = fetchLyrics(data, ranked.front().song, cancelled);
            abortIf(cancelled);
            saveAutomatic(data, track, ranked.front(), cancelled);
            return doc;
        }
        catch (const MissingLyrics &)
        {
            auto key = candidateKey(ranked.front().song);
            missing.insert(key);
            merged.erase(std::remove_if(merged.begin(), merged.end(),
                                        [&](const auto &song) { return candidateKey(song) == key; }),
                         merged.end());
            return {};
        }
    };
    auto plans = matching::queryPlans(track, aliases);
    int previousPhase = 0;
    for (auto &plan : plans)
    {
        abortIf(cancelled);
        if (previousPhase != 0 && plan.phase != previousPhase)
            if (auto doc = decide())
                return *doc;
        previousPhase = plan.phase;
        try
        {
            auto items = searchPage(plan.keyword, 1, 20, cancelled);
            merge(items);
            auto ranked = matching::rank(track, merged, aliases);
            bool poor = ranked.empty() || ranked.front().score < 65 ||
                        ((ranked.front().flags & ~matching::versions(track.title, track.album)) &
                         (matching::Japanese | matching::Cover | matching::Remix | matching::Instrumental |
                          matching::SpedUp | matching::Slowed)) ||
                        (ranked.front().artist < 55 && ranked.front().title < 60);
            if (items.size() == 20 && poor && merged.size() < 60)
                merge(searchPage(plan.keyword, 2, 20, cancelled));
        }
        catch (const std::exception &e)
        {
            abortIf(cancelled);
            networkError = e.what();
        }
        if (merged.size() >= 60)
            break;
    }
    if (auto doc = decide())
        return *doc;
    return {{}, "", merged.empty() ? networkError : ""};
}
Json Library::search(const std::string &query, int page, const Snapshot *track)
{
    if (query.size() > 2048)
        throw std::runtime_error("搜索文字过长");
    auto results = searchPage(query, std::clamp(page, 1, 5), 20);
    if (!track)
        return results;
    matching::Aliases aliases;
    aliases.load(readJson(data / L"Cache" / L"artist-aliases.json"));
    auto ranked = matching::rank(*track, results, aliases);
    Json sorted = Json::array();
    for (auto &candidate : ranked)
    {
        auto song = candidate.song;
        song["score"] = candidate.score;
        song["titleScore"] = candidate.title;
        song["artistScore"] = candidate.artist;
        song["durationScore"] = candidate.duration;
        song["albumScore"] = candidate.album;
        song["confidence"] = candidate.high          ? "High"
                             : candidate.score >= 80 ? "Medium"
                             : candidate.score >= 65 ? "Low"
                                                     : "Rejected";
        song["version"] = matching::describe(candidate.flags);
        sorted.push_back(std::move(song));
    }
    return sorted;
}
Document Library::download(const Json &candidate)
{
    return fetchLyrics(data, candidate);
}
void Library::clearManual(const Snapshot *track)
{
    std::lock_guard lock(persistenceMutex);
    auto path = data / L"Cache" / L"track-index.json";
    auto index = readJson(path);
    if (index.is_object() && index.contains("tracks") && index["tracks"].is_object())
    {
        auto title = track ? normalized(track->title) : "", artist = track ? normalized(track->artist) : "";
        auto &items = index["tracks"];
        for (auto it = items.begin(); it != items.end();)
        {
            auto &entry = it.value();
            bool manual = entry.value("matchSource", "") == "Manual";
            bool matches = !track || (entry.value("titleNormalized", "") == title &&
                                      entry.value("artistNormalized", "") == artist);
            if (manual && matches)
                it = items.erase(it);
            else
                ++it;
        }
        saveJson(path, index);
    }
    if (track)
        fs::remove(importedLyricsPath(data, track->key()));
    else
    {
        auto target = fs::weakly_canonical(data / L"Imported"), parent = fs::weakly_canonical(data);
        if (target.parent_path() != parent || target.filename() != L"Imported")
            throw std::runtime_error("无效的导入歌词目录");
        fs::remove_all(target);
    }
}
Document Library::importLrc(const fs::path &path, const std::string &key)
{
    auto text = readText(path);
    if (text.size() > 8 * 1024 * 1024)
        throw std::runtime_error("歌词文件过大");
    auto lines = parseLrc(text);
    if (lines.empty())
        throw std::runtime_error("文件中没有带时间的 LRC 歌词");
    auto dest = importedLyricsPath(data, key);
    std::lock_guard lock(persistenceMutex);
    writeText(dest, text);
    return {std::move(lines), "手动导入", "", "import-" + dest.stem().string(), false};
}
} // namespace lyrics
