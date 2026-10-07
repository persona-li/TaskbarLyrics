#include "platform.hpp"
#include <iostream>
using namespace lyrics;
void check(bool value, const char *name)
{
    if (!value)
        throw std::runtime_error(name);
}
int main(int argc, char **argv)
{
    wchar_t tmp[MAX_PATH]{};
    GetTempPathW(MAX_PATH, tmp);
    auto root = fs::path(tmp) / (L"TaskbarLyricsNativeTests-" + std::to_wstring(GetCurrentProcessId()));
    try
    {
        auto isolated = root / L"native", legacy = root / L"legacy";
        fs::create_directories(legacy / L"Cache" / L"Lyrics" / L"qq-ABC");
        saveJson(legacy / L"Cache" / L"Lyrics" / L"qq-ABC" / L"parsed-qrc.json",
                 Json::array({{{"text", "测试"},
                               {"startMs", 1000},
                               {"durationMs", 1000},
                               {"words", Json::array({{{"text", "测"}, {"startMs", 1000}, {"durationMs", 500}},
                                                      {{"text", "试"}, {"startMs", 1500}, {"durationMs", 500}}})}}}));
        Json a = {{"titleNormalized", "song"},     {"artistNormalized", "artist"}, {"album", "album"},
                  {"matchedDurationSeconds", 120}, {"matchSource", "Automatic"},   {"songMid", "ABC"}};
        Json b = a;
        b["songMid"] = "DEF";
        Json manual = b;
        manual["songMid"] = "ABC";
        manual["matchSource"] = "Manual";
        saveJson(legacy / L"Cache" / L"track-index.json", {{"tracks", {{"a", a}, {"b", b}, {"z", manual}}}});
        auto original = readText(legacy / L"Cache" / L"track-index.json");
        Library library(isolated, legacy);
        Snapshot s;
        s.title = "Song";
        s.artist = "Artist";
        s.album = "album";
        s.duration = 120000;
        auto doc = library.load(s, defaultSettings());
        check(doc.lines.size() == 1 && doc.lines[0].words.size() == 2 && doc.origin == "原版手动匹配",
              "manual wins even after conflicting automatic records");
        saveJson(legacy / L"Cache" / L"track-index.json", {{"tracks", {{"a", a}, {"b", a}}}});
        doc = library.load(s, defaultSettings());
        check(doc.lines.size() == 1, "duplicate same version is not ambiguous");
        saveJson(legacy / L"Cache" / L"track-index.json", {{"tracks", {{"a", a}, {"b", b}}}});
        doc = library.load(s, defaultSettings());
        check(doc.lines.empty() && !doc.error.empty(), "distinct automatic versions remain ambiguous");
        saveJson(legacy / L"Cache" / L"track-index.json", {{"tracks", {{"a", a}}}});
        s.album = "other";
        check(library.load(s, defaultSettings()).lines.empty(), "automatic cache album mismatch rejected");
        s.album = "album";
        s.duration = 123000;
        check(library.load(s, defaultSettings()).lines.empty(), "duration mismatch rejected");
        auto lrc = root / L"test.lrc";
        {
            std::ofstream f(lrc);
            f << "[00:01.00]测试\n[00:02.00]完成";
        }
        auto imported = library.importLrc(lrc, s.key());
        check(imported.lines.size() == 2 && library.load(s, defaultSettings()).origin == "手动导入",
              "import persisted independently");
        check(fs::exists(legacy / L"Cache" / L"track-index.json") && !fs::exists(legacy / L"Imported"),
              "native persistence does not touch legacy data");
        if (argc > 1 && std::string(argv[1]) == "--live")
        {
            auto results = library.search("BTS Butter");
            check(results.is_array() && !results.empty(), "live search");
            auto d = library.download(results.front());
            check(!d.lines.empty(), "live LRC download");
            std::cout << "Live search/LRC passed; results=" << results.size() << '\n';
        }
        std::cout << "Native storage isolation checks passed\n";
        fs::remove_all(root);
        return 0;
    }
    catch (const std::exception &e)
    {
        std::cerr << e.what() << '\n';
        fs::remove_all(root);
        return 1;
    }
}
