#include "logger.hpp"
#include "lyrics_library.hpp"
#include "platform.hpp"
#include "settings.hpp"
#include "thumbnail.hpp"
#include "tray_menu.hpp"
#include <WebView2.h>
#include <commdlg.h>
#include <dwmapi.h>
#include <dwrite.h>
#include <future>
#include <optional>
#include <shellapi.h>
#include <shlobj.h>
namespace lyrics
{
constexpr UINT MediaMessage = WM_APP + 1, ResultMessage = WM_APP + 2, TrayMessage = WM_APP + 3,
               ExitMessage = WM_APP + 4, ShowMessage = WM_APP + 5, OpenTrayMessage = WM_APP + 6;
struct Result
{
    uint64_t generation{};
    std::string type, key;
    Document document;
    Json results, candidate;
};
struct App
{
    HWND window{};
    fs::path folder, data, legacy;
    Json settings;
    Snapshot snapshot;
    std::optional<double> discardedDuration;
    Document document;
    std::string notice;
    std::string saveError;
    uint64_t generation{}, searchGeneration{}, sentGeneration = UINT64_MAX;
    std::atomic<uint64_t> workEpoch{};
    bool ready{}, loading{}, closing{}, smoke{}, controllerCreating{};
    unsigned smokeStage{};
    Json uiChecks = Json::object();
    bool savePending{};
    double saveAfter{};
    double lastState{}, started = ticks(), hiddenAt{};
    std::unique_ptr<Media> media;
    std::unique_ptr<Overlay> overlay;
    std::unique_ptr<Library> library;
    std::unique_ptr<Thumbnail> thumbnail;
    std::unique_ptr<Logger> logger;
    Json statistics = Json::object();
    std::vector<std::future<void>> jobs;
    ComPtr<ICoreWebView2Controller> controller;
    ComPtr<ICoreWebView2> web;
    ComPtr<ICoreWebView2Environment> environment;
    NOTIFYICONDATAW tray{};
    Json fonts = Json::array();
    explicit App(bool smokeMode = false) : smoke(smokeMode)
    {
        wchar_t exe[32768];
        GetModuleFileNameW(nullptr, exe, 32768);
        folder = fs::path(exe).parent_path();
        data = folder / L"Data";
        wchar_t local[32768]{};
        GetEnvironmentVariableW(L"LOCALAPPDATA", local, 32768);
        legacy = fs::path(local) / L"TaskbarLyrics";
        if (!fs::exists(folder / L"portable.flag"))
            data = fs::path(local) / L"TaskbarLyricsNative";
        // Prefer adjacent original portable data, but use it strictly read-only.
        if (fs::exists(folder.parent_path() / L"portable.flag"))
            legacy = folder.parent_path() / L"Data";
        settings = defaultSettings();
        auto saved = readJson(data / L"config.json");
        if (saved.is_object())
            settings.update(saved);
        else
            saved = Json::object();
        if (saved.empty())
            settings = migrateSettings(readJson(legacy / L"config.json"));
        normalize();
        if (smoke)
        {
            wchar_t temp[MAX_PATH]{};
            GetTempPathW(MAX_PATH, temp);
            data = fs::path(temp) / (L"TaskbarLyricsNativeSmoke-" + std::to_wstring(GetCurrentProcessId()));
            settings["overlay"] = false;
        }
        // Each architecture owns its working copy; migration never writes to WPF data.
        if (!smoke && !fs::exists(data / L"migration-v1.json"))
        {
            fs::create_directories(data);
            if (fs::exists(legacy / L"Cache"))
                fs::copy(legacy / L"Cache", data / L"Cache",
                         fs::copy_options::recursive | fs::copy_options::skip_existing);
            settings = migrateSettings(readJson(legacy / L"config.json"));
            if (saved.is_object())
                settings.update(saved);
            auto offsets = readJson(data / L"Cache" / L"track-settings.json");
            if (offsets.is_object() && offsets.contains("tracks") && offsets["tracks"].is_object())
                for (auto &x : offsets["tracks"].items())
                    settings["trackOffsets"][x.key()] = x.value().value("lyricsOffsetMs", 0);
            normalize();
            saveJson(data / L"config.json", settings);
            saveError.clear();
            saveJson(data / L"migration-v1.json", {{"version", 1}, {"copied", true}});
        }
        library = std::make_unique<Library>(data, smoke ? legacy : data);
        if (!smoke)
        {
            DWORD size = 0;
            settings["startup"] =
                RegGetValueW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Run",
                             L"TaskbarLyrics.Native", RRF_RT_REG_SZ, nullptr, nullptr, &size) == ERROR_SUCCESS &&
                size > sizeof(wchar_t);
        }
        refreshStatistics();
        logger = std::make_unique<Logger>(data / L"Logs");
        logger->write("TaskbarLyrics native 1.0.0 started");
    }
    void normalize()
    {
        normalizeSettings(settings);
        auto defaults = defaultSettings();
        for (auto &item : defaults.items())
        {
            auto &v = settings[item.key()];
            if (v.type() != item.value().type() && !(v.is_number() && item.value().is_number()))
                v = item.value();
        }
        settings["fontSize"] = std::clamp(settings.value("fontSize", 14.), 10., 72.);
        settings["globalOffset"] = std::clamp(settings.value("globalOffset", 0), -5000, 5000);
        for (auto &x : settings["trackOffsets"])
            if (x.is_number())
                x = std::clamp(x.get<int>(), -5000, 5000);
            else
                x = 0;
        for (auto name : {"normal", "highlight"})
        {
            auto s = settings.value(name, std::string("#FF496DBF"));
            if (!std::regex_match(s, std::regex("#[0-9A-Fa-f]{8}")))
                settings[name] = defaults[name];
        }
        if (settings["font"].get<std::string>().size() > 256)
            settings["font"] = defaults["font"];
    }
    void save()
    {
        savePending = false;
        try
        {
            normalize();
            saveJson(data / L"config.json", settings);
            saveError.clear();
        }
        catch (const std::exception &e)
        {
            saveError = e.what();
        }
    }
    void scheduleSave()
    {
        savePending = true;
        saveAfter = ticks() + 200;
    }
    double position() const
    {
        return std::clamp(snapshot.position +
                              (snapshot.playing ? (ticks() - snapshot.captured) * snapshot.playbackRate : 0),
                          0., snapshot.duration > 0 ? snapshot.duration : std::max(snapshot.position, 86400000.));
    }
    double effective() const
    {
        return position() + settings.value("globalOffset", 0) + settings["trackOffsets"].value(offsetKey(), 0);
    }
    std::string offsetKey() const
    {
        return document.cacheKey.empty() ? snapshot.key() : document.cacheKey;
    }
    void refreshStatistics()
    {
        uintmax_t size = 0;
        int count = 0;
        for (auto root : {data / L"Cache" / L"Lyrics", data / L"Lyrics"})
            if (fs::exists(root))
            {
                for (auto &item : fs::directory_iterator(root))
                    if (item.is_directory())
                        ++count;
                for (auto &item : fs::recursive_directory_iterator(root))
                    if (item.is_regular_file())
                        size += item.file_size();
            }
        int manual = static_cast<int>(settings["manual"].size());
        auto index = readJson(data / L"Cache" / L"track-index.json");
        if (index.is_object() && index.contains("tracks") && index["tracks"].is_object())
            for (auto &x : index["tracks"])
                if (x.value("matchSource", "Automatic") == "Manual")
                    ++manual;
        std::ostringstream bytes;
        bytes << std::fixed << std::setprecision(1) << size / 1048576. << " MB";
        statistics = {{"cacheCount", count},
                      {"cacheSize", bytes.str()},
                      {"manualCount", manual},
                      {"offsetCount", settings["trackOffsets"].size()}};
    }
    void post(const Json &message)
    {
        if (web && ready)
            web->PostWebMessageAsJson(wide(message.dump()).c_str());
    }
    void publish()
    {
        auto publicSettings = settings;
        publicSettings.erase("manual");
        Json state = {{"title", snapshot.title.empty() ? "连接播放器" : snapshot.title},
                      {"artist", snapshot.artist},
                      {"album", snapshot.album},
                      {"source", snapshot.source},
                      {"key", snapshot.title.empty() ? "" : snapshot.key()},
                      {"position", position()},
                      {"duration", snapshot.duration},
                      {"playing", snapshot.presentationPlaying},
                      {"rawPlaying", snapshot.playing},
                      {"rawStatus", snapshot.status},
                      {"playbackRate", snapshot.playbackRate},
                      {"offsetKey", offsetKey()},
                      {"toggle", snapshot.toggle},
                      {"previous", snapshot.previous},
                      {"next", snapshot.next},
                      {"seek", snapshot.seek},
                      {"captured", ticks()},
                      {"settings", publicSettings},
                      {"origin", document.origin},
                      {"error", !notice.empty() ? notice : (snapshot.error.empty() ? document.error : snapshot.error)},
                      {"loading", loading}};
        state["instrumental"] = document.instrumental;
        state["saveError"] = saveError;
        state.update(statistics);
        state["systemTheme"] = systemLight() ? "light" : "dark";
        state["version"] = "1.0.0";
        state["maximized"] = IsZoomed(window) != FALSE;
        state["layoutMeasured"] = overlay ? overlay->layoutState().value("measured", false) : false;
        if (sentGeneration != generation)
        {
            Json lines = Json::array();
            for (auto &line : document.lines)
            {
                Json words = Json::array();
                for (auto &w : line.words)
                    words.push_back({{"text", w.text}, {"start", w.start}, {"duration", w.duration}});
                lines.push_back({{"text", line.text},
                                 {"translation", line.translation},
                                 {"start", line.start},
                                 {"duration", line.duration},
                                 {"words", words}});
            }
            state["lines"] = lines;
            if (ready)
                sentGeneration = generation;
        }
        post({{"type", "state"}, {"state", state}});
        lastState = ticks();
    }
    void job(std::function<Result()> action, Result context = {})
    {
        jobs.push_back(std::async(std::launch::async, [this, action = std::move(action), context] {
            auto r = std::make_unique<Result>(context);
            try
            {
                *r = action();
            }
            catch (const std::exception &e)
            {
                r->document.error = e.what();
            }
            if (!PostMessageW(window, ResultMessage, 0, reinterpret_cast<LPARAM>(r.get())))
                return;
            r.release();
        }));
    }
    void load()
    {
        document = {};
        loading = true;
        auto t = snapshot;
        if (discardedDuration && t.duration == *discardedDuration)
            t.duration = 0;
        auto config = settings;
        auto g = generation;
        workEpoch.store(g);
        job(
            [this, t, config, g] {
                auto cancelled = [this, g] { return workEpoch.load() != g; };
                auto doc = library->load(t, config);
                if (doc.lines.empty() && !doc.instrumental && !cancelled())
                    doc = library->resolve(t, config, cancelled);
                return Result{g, "lyrics", t.key(), std::move(doc), {}, {}};
            },
            Result{g, "lyrics", t.key()});
    }
    void command(const Json &c)
    {
        auto type = c.value("type", "");
        if (type == "uiError")
        {
            logger->write("UI error: " + c.value("error", std::string("unknown")));
        }
        else if (type == "ready")
        {
            ready = true;
            sentGeneration = UINT64_MAX;
            publish();
            post({{"type", "fonts"}, {"fonts", fonts}});
            Json schemes = Json::array(), band = Json::array();
            for (auto h : {264.540965113, 305., 345., 25., 65., 105., 185., 225.})
            {
                auto p = palette(h);
                schemes.push_back({{"normal", p.first}, {"highlight", p.second}});
            }
            for (int h = 0; h <= 360; ++h)
            {
                auto p = palette(h);
                band.push_back(p.first);
            }
            post({{"type", "palettes"}, {"palettes", schemes}, {"band", band}});
        }
        else if (type == "settings")
        {
            auto values = c.at("values");
            auto allowed = defaultSettings();
            for (auto &item : values.items())
                if (allowed.contains(item.key()) && item.key() != "manual")
                    settings[item.key()] = item.value();
            if (values.contains("font"))
                settings["fonts"]["chinese"] = values["font"];
            normalize();
            if (values.contains("startup") && !smoke)
                setStartup(folder / L"TaskbarLyrics.Native.exe", settings.value("startup", false));
            scheduleSave();
            if (values.contains("trackOffsets"))
                statistics["offsetCount"] = settings["trackOffsets"].size();
            publish();
        }
        else if (type == "retrySave")
        {
            save();
            publish();
        }
        else if (type == "refreshData")
        {
            refreshStatistics();
            publish();
        }
        else if (type == "window")
        {
            auto action = c.value("action", "");
            if (action == "minimize")
                ShowWindow(window, SW_MINIMIZE);
            else if (action == "maximize")
                ShowWindow(window, IsZoomed(window) ? SW_RESTORE : SW_MAXIMIZE);
            else if (action == "close")
                PostMessageW(window, WM_CLOSE, 0, 0);
            else if (action == "drag")
            {
                ReleaseCapture();
                SendMessageW(window, WM_NCLBUTTONDOWN, HTCAPTION, 0);
            }
            else if (action == "resize" && !IsZoomed(window))
            {
                const std::map<std::string, int> edges = {{"left", HTLEFT}, {"right", HTRIGHT}, {"top", HTTOP},
                    {"bottom", HTBOTTOM}, {"top-left", HTTOPLEFT}, {"top-right", HTTOPRIGHT},
                    {"bottom-left", HTBOTTOMLEFT}, {"bottom-right", HTBOTTOMRIGHT}};
                auto edge = edges.find(c.value("edge", ""));
                if (edge != edges.end())
                {
                    POINT point{}; GetCursorPos(&point); ReleaseCapture();
                    SendMessageW(window, WM_NCLBUTTONDOWN, edge->second, MAKELPARAM(point.x, point.y));
                }
            }
        }
        else if (type == "openLink")
        {
            auto url = c.value("url", "");
            if (url == "https://github.com/persona-li/TaskbarLyrics" ||
                url == "https://github.com/persona-li/TaskbarLyrics/releases")
                ShellExecuteW(window, L"open", wide(url).c_str(), nullptr, nullptr, SW_SHOWNORMAL);
        }
        else if (type == "clearData")
        {
            workEpoch.fetch_add(1);
            for (auto &task : jobs)
                task.wait();
            auto operation = c.value("operation", "");
            if (operation == "ClearOffsets")
                settings["trackOffsets"] = Json::object();
            else if (operation == "ClearLyrics")
            {
                for (auto path : {data / L"Cache" / L"Lyrics", data / L"Lyrics"})
                {
                    auto resolved = fs::weakly_canonical(path);
                    auto root = fs::weakly_canonical(data);
                    if (resolved.parent_path() != root && resolved.parent_path() != root / L"Cache")
                        throw std::runtime_error("Invalid cache directory");
                    if (fs::exists(resolved))
                        fs::remove_all(resolved);
                }
            }
            else if (operation == "ClearCurrentManual" || operation == "ClearAllManual")
            {
                if (operation == "ClearAllManual")
                    settings["manual"] = Json::object();
                else
                    settings["manual"].erase(snapshot.key());
                library->clearManual(operation == "ClearAllManual" ? nullptr : &snapshot);
            }
            save();
            refreshStatistics();
            ++generation;
            workEpoch.store(generation);
            load();
            publish();
        }
        else if (type == "palette")
        {
            auto h = hue(c.value("hue", 264.540965113));
            auto pair = palette(h);
            settings["hue"] = h;
            settings["normal"] = pair.first;
            settings["highlight"] = pair.second;
            scheduleSave();
            publish();
        }
        else if (type == "reload")
        {
            ++generation;
            load();
            publish();
        }
        else if (type == "search")
        {
            auto query = c.value("query", "");
            if (query.size() > 512)
                return;
            auto serial = ++searchGeneration;
            auto page = std::clamp(c.value("page", 1), 1, 100);
            bool append = c.value("append", false);
            auto searchTrack = snapshot;
            if (discardedDuration && searchTrack.duration == *discardedDuration)
                searchTrack.duration = 0;
            job(
                [this, query, serial, page, append, searchTrack] {
                    Result r;
                    r.type = "search";
                    r.generation = serial;
                    r.results = library->search(query, page, &searchTrack);
                    r.candidate = {{"append", append}, {"hasMore", r.results.size() >= 20}};
                    return r;
                },
                Result{serial, "search"});
        }
        else if (type == "apply")
        {
            auto key = c.value("key", "");
            if (key != snapshot.key())
                return;
            auto candidate = c.at("candidate");
            auto g = ++generation;
            workEpoch.store(g);
            loading = true;
            job([this, key, candidate,
                 g] { return Result{g, "manual", key, library->download(candidate), {}, candidate}; },
                Result{g, "manual", key});
            publish();
        }
        else if (type == "import")
        {
            if (snapshot.title.empty())
                return;
            wchar_t path[32768]{};
            OPENFILENAMEW ofn{sizeof(ofn)};
            ofn.hwndOwner = window;
            ofn.lpstrFilter = L"LRC 歌词\0*.lrc\0所有文件\0*.*\0";
            ofn.lpstrFile = path;
            ofn.nMaxFile = 32768;
            ofn.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_NOCHANGEDIR;
            if (GetOpenFileNameW(&ofn))
            {
                ++generation;
                document = library->importLrc(path, snapshot.key());
                settings["manual"].erase(snapshot.key());
                save();
                loading = false;
                publish();
            }
        }
        else if (type == "openData")
        {
            fs::create_directories(data);
            auto path = data / L"Cache" / L"Lyrics";
            fs::create_directories(path);
            ShellExecuteW(window, L"open", path.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
        }
        else if (type == "resetColors")
        {
            auto defaults = defaultSettings();
            for (auto name : {"normal", "highlight", "hue", "shadowColor"})
                settings[name] = defaults[name];
            save();
            publish();
        }
        else if (type == "resetAppearance")
        {
            auto defaults = defaultSettings();
            for (auto name : {"font",       "fonts",       "fontSize",    "normal",        "highlight",
                              "hue",        "shadow",      "shadowColor", "shadowOffsetX", "shadowOffsetY",
                              "alignment",  "karaoke",     "autoPan",     "autoLayout",    "verticalOffset",
                              "leftMargin", "rightMargin", "minWidth",    "maxWidth",      "fallbackWidthPercent"})
                settings[name] = defaults[name];
            save();
            publish();
        }
        else if (type == "toggle" || type == "previous" || type == "next" || type == "seek")
        {
            if (type == "seek" && (!snapshot.seek || !c.contains("position") || !c["position"].is_number()))
                return;
            media->command(c);
        }
    }
    void received(Snapshot value)
    {
        bool changed = value.key() != snapshot.key() || value.source != snapshot.source;
        bool identityChanged = value.title != snapshot.title || value.artist != snapshot.artist;
        bool durationCorrected = discardedDuration && value.duration != *discardedDuration && value.duration > 0;
        if (changed)
        {
            if (identityChanged && snapshot.duration > 0 && snapshot.duration == value.duration)
                discardedDuration = value.duration;
            else
                discardedDuration.reset();
        }
        else if (durationCorrected)
            discardedDuration.reset();
        auto error = snapshot.error;
        snapshot = std::move(value);
        if (changed)
        {
            if (logger)
                logger->write("Media track changed; source=" + snapshot.source);
            ++generation;
            workEpoch.store(generation);
            document = {};
            loading = false;
            if (!snapshot.title.empty())
                load();
        }
        if (changed || ticks() - lastState >= 190)
            publish();
        if (!changed && durationCorrected && document.lines.empty() && !document.instrumental && !loading &&
            !snapshot.title.empty())
        {
            ++generation;
            load();
            publish();
        }
    }
    void result(Result r)
    {
        if (r.type == "search")
        {
            if (r.generation != searchGeneration)
                return;
            notice = r.document.error;
            post({{"type", "search"},
                  {"results", r.results.is_array() ? r.results : Json::array()},
                  {"append", r.candidate.value("append", false)},
                  {"hasMore", r.candidate.value("hasMore", false)}});
            publish();
            return;
        }
        if (!accepts(generation, snapshot.key(), r.generation, r.key))
        {
            if (r.type == "manual")
                post({{"type", "applied"}, {"ok", false}, {"key", r.key}, {"error", "歌曲已切换，请重新选择"}});
            return;
        }
        document = std::move(r.document);
        if (logger)
            logger->write("Lyrics loaded; origin=" + document.origin +
                          " lines=" + std::to_string(document.lines.size()) + " error=" + document.error);
        sentGeneration = UINT64_MAX;
        loading = false;
        if (r.type == "manual" && (!document.lines.empty() || document.instrumental))
        {
            settings["manual"][r.key] = r.candidate;
            save();
        }
        if (r.type == "manual")
            post({{"type", "applied"},
                  {"key", r.key},
                  {"ok", document.error.empty() && (!document.lines.empty() || document.instrumental)},
                  {"error", document.error}});
        publish();
        refreshStatistics();
    }
    void enumerateFonts()
    {
        ComPtr<IDWriteFactory> factory;
        DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory),
                            reinterpret_cast<IUnknown **>(factory.GetAddressOf()));
        ComPtr<IDWriteFontCollection> collection;
        if (!factory || FAILED(factory->GetSystemFontCollection(collection.GetAddressOf())))
            return;
        std::vector<std::string> names;
        for (UINT32 i = 0; i < collection->GetFontFamilyCount(); ++i)
        {
            ComPtr<IDWriteFontFamily> family;
            ComPtr<IDWriteLocalizedStrings> strings;
            collection->GetFontFamily(i, family.GetAddressOf());
            family->GetFamilyNames(strings.GetAddressOf());
            UINT32 index = 0;
            BOOL found = FALSE;
            strings->FindLocaleName(L"zh-cn", &index, &found);
            if (!found)
                strings->FindLocaleName(L"en-us", &index, &found);
            if (!found)
                index = 0;
            UINT32 length{};
            strings->GetStringLength(index, &length);
            std::wstring name(length + 1, L'\0');
            strings->GetString(index, name.data(), length + 1);
            name.resize(length);
            names.push_back(utf8(name));
        }
        std::sort(names.begin(), names.end());
        fonts = names;
    }
    void show()
    {
        hiddenAt = 0;
        ShowWindow(window, SW_SHOW);
        SetForegroundWindow(window);
        if (!controller)
            createWeb();
        else if (controller)
        {
            RECT bounds{};
            GetClientRect(window, &bounds);
            controller->put_Bounds(bounds);
            controller->put_IsVisible(TRUE);
        }
    }
    void checkRenderedUi(const std::string &phase)
    {
        if (!web || !controller)
        {
            uiChecks[phase] = {{"rendered", false}, {"controllerVisible", false}};
            return;
        }
        BOOL visible = FALSE;
        controller->get_IsVisible(&visible);
        RECT bounds{};
        GetWindowRect(window, &bounds);
        double scale = GetDpiForWindow(window) / 96.;
        double windowWidth = (bounds.right - bounds.left) / scale;
        double windowHeight = (bounds.bottom - bounds.top) / scale;
        const auto windowStyle = GetWindowLongPtrW(window, GWL_STYLE);
        const bool nativeCaption = (windowStyle & WS_CAPTION) == WS_CAPTION;
        const bool resizable = (windowStyle & WS_THICKFRAME) != 0;
        web->ExecuteScript(
            LR"((() => {
                const nav = document.querySelector('nav'), main = document.querySelector('main');
                const rect = main?.getBoundingClientRect();
                return {rendered: !!nav && nav.querySelectorAll('button').length >= 4 &&
                    !!main && !!main.querySelector('h1') && rect.width > 0 && rect.height > 0,
                    heading: main?.querySelector('h1')?.textContent || '',
                    width: rect?.width || 0, height: rect?.height || 0};
            })())",
            Microsoft::WRL::Callback<ICoreWebView2ExecuteScriptCompletedHandler>(
                [this, phase, visible, windowWidth, windowHeight, nativeCaption, resizable](HRESULT result, LPCWSTR raw) -> HRESULT {
                    Json check = SUCCEEDED(result) && raw ? Json::parse(utf8(raw), nullptr, false) : Json();
                    if (!check.is_object())
                        check = {{"rendered", false}};
                    check["controllerVisible"] = visible != FALSE;
                    check["windowWidthDip"] = windowWidth;
                    check["windowHeightDip"] = windowHeight;
                    check["nativeCaption"] = nativeCaption;
                    check["resizable"] = resizable;
                    uiChecks[phase] = check;
                    logger->write("UI check " + phase + ": " + check.dump());
                    return S_OK;
                }).Get());
    }
    void clampWindow()
    {
        RECT r{};
        GetWindowRect(window, &r);
        MONITORINFO area{sizeof(area)};
        GetMonitorInfoW(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST), &area);
        int width = std::min(r.right - r.left, area.rcWork.right - area.rcWork.left),
            height = std::min(r.bottom - r.top, area.rcWork.bottom - area.rcWork.top);
        int left = std::clamp(r.left, area.rcWork.left, area.rcWork.right - width),
            top = std::clamp(r.top, area.rcWork.top, area.rcWork.bottom - height);
        SetWindowPos(window, nullptr, left, top, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
    }
    void rememberWindow()
    {
        if (smoke)
            return;
        WINDOWPLACEMENT p{sizeof(p)};
        if (GetWindowPlacement(window, &p))
        {
            try
            {
                auto r = p.rcNormalPosition;
                saveJson(data / L"window.json",
                         {{"left", r.left}, {"top", r.top}, {"width", r.right - r.left}, {"height", r.bottom - r.top}});
            }
            catch (...)
            {
            }
        }
    }
    void tick()
    {
        if (smoke)
        {
            double elapsed = ticks() - started;
            if (smokeStage == 0 && elapsed > 5000)
            {
                checkRenderedUi("initial");
                ++smokeStage;
            }
            else if (smokeStage == 1 && elapsed > 7000)
            {
                SendMessageW(window, WM_CLOSE, 0, 0);
                PostMessageW(window, ShowMessage, 0, 0);
                ++smokeStage;
            }
            else if (smokeStage == 2 && elapsed > 9000)
            {
                checkRenderedUi("reopenCached");
                ++smokeStage;
            }
            else if (smokeStage == 3 && elapsed > 11000)
            {
                SendMessageW(window, WM_CLOSE, 0, 0);
                // Exercise the normal 30-second release without prolonging a smoke run.
                hiddenAt = ticks() - 31000;
                ++smokeStage;
            }
            else if (smokeStage == 4 && elapsed > 12000)
            {
                PostMessageW(window, ShowMessage, 0, 0);
                ++smokeStage;
            }
            else if (smokeStage == 5 && elapsed > 21000)
            {
                checkRenderedUi("reopenReleased");
                ++smokeStage;
            }
        }
        if (savePending && ticks() >= saveAfter)
            save();
        if (overlay)
        {
            auto rendering = settings;
            rendering["fallbackText"] = snapshot.title.empty()  ? ""
                                        : document.instrumental ? "♪ 纯音乐"
                                                                : ("♪ " + snapshot.title);
            overlay->update(document.lines, effective(), rendering);
        }
        if (thumbnail)
            thumbnail->update(snapshot, document, effective());
        for (auto i = jobs.begin(); i != jobs.end();)
            if (i->wait_for(std::chrono::seconds(0)) == std::future_status::ready)
            {
                i->get();
                i = jobs.erase(i);
            }
            else
                ++i;
        if (hiddenAt && ticks() - hiddenAt > 30000 && controller)
        {
            ready = false;
            controller->Close();
            controller.Reset();
            web.Reset();
            // Keep the lightweight environment while releasing the page renderer.
            // Recreating the environment can race the old browser process closing.
        }
        if (smoke && ticks() - started > 23000)
        {
            saveJson(folder / L"smoke-summary.json",
                     {{"webReady", ready},
                      {"uiChecks", uiChecks},
                      {"mediaPresent", !snapshot.title.empty()},
                      {"lyricLines", document.lines.size()},
                      {"playing", snapshot.playing},
                      {"error", snapshot.error.empty() ? document.error : snapshot.error},
                      {"nativeOnly", true}});
            DestroyWindow(window);
        }
    }
    HRESULT createController(ICoreWebView2Environment *env)
    {
        return env->CreateCoreWebView2Controller(
                        window,
                        Microsoft::WRL::Callback<ICoreWebView2CreateCoreWebView2ControllerCompletedHandler>(
                            [this](HRESULT h, ICoreWebView2Controller *control) -> HRESULT {
                                logger->write("WebView controller result=" + std::to_string(h));
                                controllerCreating = false;
                                if (FAILED(h) || !control)
                                    return h;
                                controller = control;
                                controller->get_CoreWebView2(web.GetAddressOf());
                                RECT bounds{};
                                GetClientRect(window, &bounds);
                                controller->put_Bounds(bounds);
                                controller->put_IsVisible(hiddenAt == 0);
                                ComPtr<ICoreWebView2Settings> options;
                                web->get_Settings(options.GetAddressOf());
                                options->put_AreDefaultContextMenusEnabled(FALSE);
                                options->put_AreDevToolsEnabled(FALSE);
                                options->put_IsStatusBarEnabled(FALSE);
                                ComPtr<ICoreWebView2Controller2> background;
                                if (SUCCEEDED(controller.As(&background)))
                                {
                                    bool dark =
                                        settings.value("theme", std::string("system")) == "dark" ||
                                        (settings.value("theme", std::string("system")) == "system" && !systemLight());
                                    BYTE color = dark ? 32 : 243;
                                    background->put_DefaultBackgroundColor(
                                        COREWEBVIEW2_COLOR{255, color, color, color});
                                }
                                ComPtr<ICoreWebView2Settings3> accelerators;
                                if (SUCCEEDED(options.As(&accelerators)))
                                    accelerators->put_AreBrowserAcceleratorKeysEnabled(FALSE);
                                ComPtr<ICoreWebView2Settings9> draggable;
                                if (SUCCEEDED(options.As(&draggable)))
                                    draggable->put_IsNonClientRegionSupportEnabled(TRUE);
                                ComPtr<ICoreWebView2_3> mapping;
                                if (FAILED(web.As(&mapping)))
                                    return E_NOINTERFACE;
                                auto assets = folder / L"ui";
                                mapping->SetVirtualHostNameToFolderMapping(
                                    L"taskbarlyrics.local", assets.c_str(),
                                    COREWEBVIEW2_HOST_RESOURCE_ACCESS_KIND_DENY_CORS);
                                EventRegistrationToken token{};
                                web->AddScriptToExecuteOnDocumentCreated(
                                    LR"(window.addEventListener('error', e => window.chrome.webview.postMessage({type:'uiError',error:String(e.message)}));
                                        window.addEventListener('unhandledrejection', e => window.chrome.webview.postMessage({type:'uiError',error:String(e.reason)}));)",
                                    nullptr);
                                web->add_WebMessageReceived(
                                    Microsoft::WRL::Callback<ICoreWebView2WebMessageReceivedEventHandler>(
                                        [this](ICoreWebView2 *,
                                               ICoreWebView2WebMessageReceivedEventArgs *args) -> HRESULT {
                                            LPWSTR source = nullptr, raw = nullptr;
                                            args->get_Source(&source);
                                            bool trusted = source && std::wstring(source).starts_with(
                                                                         L"https://taskbarlyrics.local/");
                                            CoTaskMemFree(source);
                                            if (!trusted)
                                                return S_OK;
                                            args->get_WebMessageAsJson(&raw);
                                            try
                                            {
                                                auto c = Json::parse(utf8(raw ? raw : L""));
                                                if (c.is_object())
                                                    command(c);
                                            }
                                            catch (const std::exception &e)
                                            {
                                                notice = e.what();
                                                publish();
                                            }
                                            CoTaskMemFree(raw);
                                            return S_OK;
                                        })
                                        .Get(),
                                    &token);
                                web->add_NavigationStarting(
                                    Microsoft::WRL::Callback<ICoreWebView2NavigationStartingEventHandler>(
                                        [](ICoreWebView2 *, ICoreWebView2NavigationStartingEventArgs *args) -> HRESULT {
                                            LPWSTR uri = nullptr;
                                            args->get_Uri(&uri);
                                            if (!uri || !std::wstring(uri).starts_with(L"https://taskbarlyrics.local/"))
                                                args->put_Cancel(TRUE);
                                            CoTaskMemFree(uri);
                                            return S_OK;
                                        })
                                        .Get(),
                                    &token);
                                web->Navigate(L"https://taskbarlyrics.local/index.html");
                                return S_OK;
                            })
                            .Get());
    }
    void createWeb()
    {
        if (controllerCreating) return;
        controllerCreating = true;
        if (environment)
        {
            auto hr = createController(environment.Get());
            if (FAILED(hr)) controllerCreating = false;
            return;
        }
        logger->write("Creating WebView controller");
        fs::create_directories(data);
        auto cache = data / L"WebView";
        auto hr = CreateCoreWebView2EnvironmentWithOptions(
            nullptr, cache.c_str(), nullptr,
            Microsoft::WRL::Callback<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>(
                [this](HRESULT result, ICoreWebView2Environment *env) -> HRESULT {
                    logger->write("WebView environment result=" + std::to_string(result));
                    if (FAILED(result) || !env)
                    {
                        controllerCreating = false;
                        MessageBoxW(window, L"无法启动 WebView2。请安装 Microsoft Edge WebView2 Runtime。",
                                    L"TaskbarLyrics", MB_OK | MB_ICONERROR);
                        return S_OK;
                    }
                    environment = env;
                    return createController(env);
                })
                .Get());
        if (FAILED(hr))
            MessageBoxW(window, L"无法加载 WebView2 Runtime。", L"TaskbarLyrics", MB_OK | MB_ICONERROR);
    }
    void init(HWND h)
    {
        window = h;
        // WPF opens a fresh 1040 x 720 DIP management window centered on screen.
        // Do not restore an unrelated physical-pixel size from the migration build.
        MONITORINFO monitor{sizeof(monitor)};
        GetMonitorInfoW(MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST), &monitor);
        auto dpi = GetDpiForWindow(h);
        int width = std::min(MulDiv(1040, dpi, 96), static_cast<int>(monitor.rcWork.right - monitor.rcWork.left));
        int height = std::min(MulDiv(720, dpi, 96), static_cast<int>(monitor.rcWork.bottom - monitor.rcWork.top));
        SetWindowPos(h, nullptr, monitor.rcWork.left + (monitor.rcWork.right - monitor.rcWork.left - width) / 2,
                     monitor.rcWork.top + (monitor.rcWork.bottom - monitor.rcWork.top - height) / 2,
                     width, height, SWP_NOZORDER | SWP_NOACTIVATE);
        clampWindow();
        enumerateFonts();
        overlay = std::make_unique<Overlay>();
        thumbnail = std::make_unique<Thumbnail>(window);
        media = std::make_unique<Media>([this](Snapshot s) {
            auto ptr = std::make_unique<Snapshot>(std::move(s));
            if (PostMessageW(window, MediaMessage, 0, reinterpret_cast<LPARAM>(ptr.get())))
                ptr.release();
        });
        tray.cbSize = sizeof(tray);
        tray.hWnd = window;
        tray.uID = 1;
        tray.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        tray.uCallbackMessage = TrayMessage;
        tray.hIcon = LoadIconW(GetModuleHandleW(nullptr), MAKEINTRESOURCEW(2));
        wcscpy_s(tray.szTip, L"TaskbarLyrics");
        Shell_NotifyIconW(NIM_ADD, &tray);
        SetTimer(window, 1, 33, nullptr);
        show();
    }
    void shutdown()
    {
        if (savePending)
            save();
        closing = true;
        workEpoch.fetch_add(1);
        KillTimer(window, 1);
        media.reset();
        for (auto &task : jobs)
            task.wait();
        jobs.clear();
        MSG message{};
        while (PeekMessageW(&message, window, MediaMessage, ResultMessage, PM_REMOVE))
        {
            if (message.message == MediaMessage)
                delete reinterpret_cast<Snapshot *>(message.lParam);
            else
                delete reinterpret_cast<Result *>(message.lParam);
        }
        overlay.reset();
        thumbnail.reset();
        if (controller)
            controller->Close();
        controller.Reset();
        web.Reset();
        environment.Reset();
        Shell_NotifyIconW(NIM_DELETE, &tray);
    }
};
LRESULT CALLBACK windowProc(HWND h, UINT m, WPARAM w, LPARAM l)
{
    auto app = reinterpret_cast<App *>(GetWindowLongPtrW(h, GWLP_USERDATA));
    if (m == WM_NCCREATE)
    {
        app = static_cast<App *>(reinterpret_cast<CREATESTRUCTW *>(l)->lpCreateParams);
        SetWindowLongPtrW(h, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(app));
    }
    if (!app)
        return DefWindowProcW(h, m, w, l);
    static UINT taskbarCreated = RegisterWindowMessageW(L"TaskbarCreated"),
                buttonCreated = RegisterWindowMessageW(L"TaskbarButtonCreated");
    if (m == taskbarCreated)
    {
        Shell_NotifyIconW(NIM_ADD, &app->tray);
        if (app->thumbnail)
            app->thumbnail->explorerRestart();
        return 0;
    }
    if (m == buttonCreated)
    {
        if (app->thumbnail)
            app->thumbnail->explorerRestart();
        return 0;
    }
    switch (m)
    {
    case WM_MEASUREITEM:
        if (TrayMenu::measure(reinterpret_cast<MEASUREITEMSTRUCT *>(l)))
            return TRUE;
        break;
    case WM_DRAWITEM:
        if (TrayMenu::draw(reinterpret_cast<DRAWITEMSTRUCT *>(l)))
            return TRUE;
        break;
    case WM_NCCALCSIZE:
        if (w)
        {
            auto p = reinterpret_cast<NCCALCSIZE_PARAMS *>(l);
            if (IsZoomed(h))
            {
                MONITORINFO mi{sizeof(mi)};
                GetMonitorInfoW(MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST), &mi);
                p->rgrc[0] = mi.rcWork;
            }
            return 0;
        }
        break;
    case WM_ERASEBKGND:
        return 1;
    case WM_PAINT: {
        PAINTSTRUCT p;
        auto dc = BeginPaint(h, &p);
        bool dark = app->settings.value("theme", std::string("system")) == "dark" ||
                    (app->settings.value("theme", std::string("system")) == "system" && !systemLight());
        auto brush = CreateSolidBrush(dark ? RGB(32, 32, 32) : RGB(243, 243, 243));
        FillRect(dc, &p.rcPaint, brush);
        DeleteObject(brush);
        EndPaint(h, &p);
        return 0;
    }
    case WM_DISPLAYCHANGE:
        app->clampWindow();
        return 0;
    case WM_EXITSIZEMOVE:
        app->rememberWindow();
        return 0;
    case WM_COMMAND:
        if (HIWORD(w) == THBN_CLICKED && LOWORD(w) >= 100 && LOWORD(w) <= 102)
        {
            app->command({{"type", LOWORD(w) == 100 ? "previous" : LOWORD(w) == 101 ? "toggle" : "next"}});
            return 0;
        }
        break;
    case WM_DWMSENDICONICTHUMBNAIL:
        if (app->thumbnail)
        {
            app->thumbnail->render(HIWORD(l), LOWORD(l), app->snapshot, app->document, app->effective());
            return 0;
        }
        break;
    case WM_SETTINGCHANGE:
    case WM_THEMECHANGED:
        app->publish();
        return 0;
    case WM_SIZE:
        if (app->controller)
        {
            RECT b;
            GetClientRect(h, &b);
            app->controller->put_Bounds(b);
        }
        if (app->ready) app->publish();
        break;
    case WM_DPICHANGED: {
        auto r = reinterpret_cast<RECT *>(l);
        SetWindowPos(h, nullptr, r->left, r->top, r->right - r->left, r->bottom - r->top, SWP_NOZORDER);
        break;
    }
    case WM_GETMINMAXINFO: {
        auto p = reinterpret_cast<MINMAXINFO *>(l);
        double scale = GetDpiForWindow(h) / 96.;
        MONITORINFO info{sizeof(info)};
        GetMonitorInfoW(MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST), &info);
        p->ptMinTrackSize = {std::min(static_cast<LONG>(760 * scale), info.rcWork.right - info.rcWork.left),
                             std::min(static_cast<LONG>(480 * scale), info.rcWork.bottom - info.rcWork.top)};
        return 0;
    }
    case MediaMessage: {
        std::unique_ptr<Snapshot> s(reinterpret_cast<Snapshot *>(l));
        app->received(std::move(*s));
        return 0;
    }
    case ResultMessage: {
        std::unique_ptr<Result> r(reinterpret_cast<Result *>(l));
        app->result(std::move(*r));
        return 0;
    }
    case WM_TIMER:
        if (w == 2)
        {
            KillTimer(h, 2);
            PostMessageW(h, OpenTrayMessage, 0, 0);
            return 0;
        }
        app->tick();
        return 0;
    case ShowMessage:
        app->show();
        return 0;
    case ExitMessage:
        DestroyWindow(h);
        return 0;
    case TrayMessage:
        if (l == WM_LBUTTONUP || l == WM_RBUTTONUP)
        {
            if (auto menu = FindWindowW(L"TaskbarLyricsNativeTrayMenu", nullptr))
                PostMessageW(menu, WM_CLOSE, 0, 0);
            else
                // Finish the opening click before showing the non-activating popup.
                SetTimer(h, 2, 120, nullptr);
        }
        return 0;
    case OpenTrayMessage:
        {
            std::wstring health = app->loading ? L"加载中" : app->document.instrumental ? L"纯音乐" :
                !app->document.lines.empty() ? L"已就绪" : !app->document.error.empty() ? L"加载失败" : L"暂无歌词";
            auto &s = app->snapshot;
            std::wstring state = s.title.empty() ? L"等待播放" :
                (s.presentationPlaying ? L"正在播放" : s.status == "Stopped" ? L"已停止" : L"已暂停") + std::wstring(L" · ") + health;
            TrayMenu menu(h, s, app->settings, state, app->loading, [app] {
                app->settings["overlay"] = !app->settings.value("overlay", true);
                app->save(); app->publish();
            });
            POINT point;
            GetCursorPos(&point);
            int command = menu.show(h, point);
            if (command == 1)
                app->show();
            else if (command == 2)
                DestroyWindow(h);
            else if (command == 3)
            {
                app->settings["overlay"] = !app->settings.value("overlay", true);
                app->save();
                app->publish();
            }
            else if (command == 5)
            {
                app->show();
                app->post({{"type", "openSearch"}});
            }
        }
        return 0;
    case WM_CLOSE:
        if (app->savePending)
            app->save();
        app->hiddenAt = ticks();
        ShowWindow(h, SW_HIDE);
        if (app->controller)
            app->controller->put_IsVisible(FALSE);
        return 0;
    case WM_DESTROY:
        app->shutdown();
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(h, m, w, l);
}
} // namespace lyrics
int WINAPI wWinMain(HINSTANCE instance, HINSTANCE, LPWSTR args, int)
{
    using namespace lyrics;
    if (std::wstring(args) == L"--shutdown")
    {
        auto h = FindWindowW(L"TaskbarLyricsNativeMain", nullptr);
        if (h)
            PostMessageW(h, ExitMessage, 0, 0);
        return 0;
    }
    CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    HANDLE singleton = CreateMutexW(nullptr, FALSE, L"Local\\TaskbarLyrics.Native.SingleInstance");
    if (GetLastError() == ERROR_ALREADY_EXISTS)
    {
        auto h = FindWindowW(L"TaskbarLyricsNativeMain", nullptr);
        if (h)
        {
            PostMessageW(h, ShowMessage, 0, 0);
        }
        CloseHandle(singleton);
        CoUninitialize();
        return 0;
    }
    int result = 0;
    try
    {
        App app(std::wstring(args) == L"--smoke");
        WNDCLASSW wc{};
        wc.hInstance = instance;
        wc.lpfnWndProc = windowProc;
        wc.lpszClassName = L"TaskbarLyricsNativeMain";
        wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        wc.hIcon = LoadIconW(instance, MAKEINTRESOURCEW(1));
        RegisterClassW(&wc);
        UINT dpi = GetDpiForSystem();
        HWND window =
            // An overlapped window implicitly gains WS_CAPTION. Use an unowned
            // popup with native resize/system commands; React owns the caption.
            CreateWindowExW(0, wc.lpszClassName, L"TaskbarLyrics", WS_POPUP | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX, CW_USEDEFAULT, CW_USEDEFAULT,
                            MulDiv(1040, dpi, 96), MulDiv(720, dpi, 96), nullptr, nullptr, instance, &app);
        if (!window)
            throw std::runtime_error("Cannot create window");
        int rounded = 2;
        DwmSetWindowAttribute(window, 33, &rounded, sizeof(rounded));
        DWORD noBorder = 0xfffffffe;
        DwmSetWindowAttribute(window, 34, &noBorder, sizeof(noBorder));
        SetWindowPos(window, nullptr, 0, 0, 0, 0,
                     SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        app.init(window);
        if (std::wstring(args) == L"--background")
            PostMessageW(window, WM_CLOSE, 0, 0);
        MSG msg;
        while (GetMessageW(&msg, nullptr, 0, 0) > 0)
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }
    catch (const std::exception &e)
    {
        MessageBoxW(nullptr, wide(e.what()).c_str(), L"TaskbarLyrics", MB_ICONERROR);
        result = 1;
    }
    CloseHandle(singleton);
    CoUninitialize();
    return result;
}
