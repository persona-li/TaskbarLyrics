#include "lyrics_library.hpp"
#include "lyrics_match.hpp"
#include "lyrics_qrc.hpp"
#include <iostream>
using namespace lyrics;
void check(bool value, const std::string &label)
{
    if (!value)
        throw std::runtime_error(label);
}
void workflows(const Json &qrcFixtures)
{
    wchar_t temp[MAX_PATH]{};
    GetTempPathW(MAX_PATH, temp);
    auto root = fs::path(temp) / (L"TaskbarLyricsNativeLyricsParity-" + std::to_wstring(GetCurrentProcessId()));
    fs::create_directories(root);
    struct Cleanup
    {
        fs::path path;
        ~Cleanup()
        {
            std::error_code e;
            fs::remove_all(path, e);
        }
    } cleanup{root};
    Library library(root, root);
    Snapshot track;
    track.title = "Fixture Signal";
    track.artist = "Fixture Artist";
    track.album = "Studio";
    track.duration = 120000;
    Json song = {{"id", "101"},          {"mid", "AUTO1"}, {"title", track.title}, {"artist", track.artist},
                 {"album", track.album}, {"duration", 120}};
    int requests = 0, searches = 0;
    bool noLyrics = false, networkFailure = false, cancel = false;
    Json offered = Json::array({song});
    auto transport = [&](const std::wstring &path) -> std::string {
        ++requests;
        auto p = utf8(path);
        if (p.find("client_search_cp") != std::string::npos)
        {
            ++searches;
            Json list = Json::array();
            for (auto &s : offered)
                list.push_back({{"songid", std::stoi(s.value("id", "0"))},
                                {"songmid", s.value("mid", "")},
                                {"songname", s.value("title", "")},
                                {"albumname", s.value("album", "")},
                                {"interval", s.value("duration", 0)},
                                {"singer", Json::array({{{"name", s.value("artist", "")}}})}});
            return Json({{"code", 0}, {"data", {{"song", {{"list", list}}}}}}).dump();
        }
        if (networkFailure)
            throw std::runtime_error("Offline fixture network failure");
        if (p.find("fcg_query_lyric_new") != std::string::npos)
        {
            if (cancel)
                cancel = true;
            if (noLyrics && p.find("musicid=103") != std::string::npos)
                return Json({{"code", -1901}}).dump();
            return Json({{"code", 0},
                         {"lyric", "[00:01.00]LRC fallback\n[00:03.00]Next"},
                         {"trans", "[00:01.00]LRC translation"}})
                .dump();
        }
        if (noLyrics && p.find("musicid=103") != std::string::npos)
            return "<root><content></content></root>";
        return "<!--<root><content>" + qrcFixtures[0].value("hex", "") +
               "</content><contentts><![CDATA[[00:01.00]QRC translation\n[00:03.00]Translated "
               "next]]></contentts></root>-->";
    };
    ScopedLibraryTransport scope(transport);
    auto doc = library.resolve(track, defaultSettings());
    check(doc.cacheKey == "qq-AUTO1" && doc.lines.size() == 2 && doc.lines[0].words.size() == 2 &&
              doc.lines[0].translation == "QRC translation",
          "Automatic workflow downloads QRC and gives its translation priority");
    auto count = requests;
    check(library.resolve(track, defaultSettings()).cacheKey == "qq-AUTO1" && requests == count,
          "Repeated track resolves entirely from cache without search");
    {
        std::ofstream f(root / L"Cache" / L"Lyrics" / L"qq-AUTO1" / L"parsed-qrc.json");
        f << "invalid";
    }
    doc = library.load(track, defaultSettings());
    check(doc.lines.size() == 2 && doc.lines[0].words.size() == 2 && requests == count &&
              readJson(root / L"Cache" / L"Lyrics" / L"qq-AUTO1" / L"parsed-qrc.json").is_array(),
          "Corrupt parser cache is repaired locally without search");
    auto manual = song;
    manual["id"] = "102";
    manual["mid"] = "MANUAL1";
    Json settings = defaultSettings();
    settings["manual"][track.key()] = manual;
    doc = library.resolve(track, settings);
    check(doc.cacheKey == "qq-MANUAL1" && searches == 1,
          "Manual selected version precedes an existing automatic binding");
    auto index = readJson(root / L"Cache" / L"track-index.json");
    auto &entry = index["tracks"].begin().value();
    entry["songMid"] = "OLD1";
    entry["songId"] = "103";
    saveJson(root / L"Cache" / L"track-index.json", index);
    auto old = song;
    old["mid"] = "OLD1";
    old["id"] = "103";
    auto replacement = song;
    replacement["mid"] = "NEW1";
    replacement["id"] = "104";
    offered = Json::array({old, replacement});
    noLyrics = true;
    doc = library.resolve(track, defaultSettings());
    check(doc.cacheKey == "qq-NEW1" &&
              readJson(root / L"Cache" / L"track-index.json")["tracks"].begin().value().value("songMid", "") == "NEW1",
          "A confirmed empty automatic binding is replaced by a different recording candidate");
    settings["manual"][track.key()] = old;
    auto beforeSearch = searches;
    bool missing = false;
    try
    {
        library.resolve(track, settings);
    }
    catch (const std::exception &)
    {
        missing = true;
    }
    check(missing && searches == beforeSearch, "An empty manual binding is never replaced automatically");
    entry["songMid"] = "OLD1";
    entry["songId"] = "103";
    saveJson(root / L"Cache" / L"track-index.json", index);
    networkFailure = true;
    beforeSearch = searches;
    bool failed = false;
    try
    {
        library.resolve(track, defaultSettings());
    }
    catch (const std::exception &)
    {
        failed = true;
    }
    check(failed && searches == beforeSearch &&
              readJson(root / L"Cache" / L"track-index.json")["tracks"].begin().value().value("songMid", "") == "OLD1",
          "A transport error does not erase or rematch an existing binding");
    bool cancelled = false;
    count = requests;
    auto saved = readText(root / L"Cache" / L"track-index.json");
    try
    {
        library.resolve(track, defaultSettings(), [] { return true; });
    }
    catch (const std::exception &)
    {
        cancelled = true;
    }
    check(cancelled && requests == count && readText(root / L"Cache" / L"track-index.json") == saved,
          "A cancelled generation cannot issue requests or modify mappings");
    networkFailure = false;
    noLyrics = false;
    auto results = library.search("Fixture Signal", 2, &track);
    check(results.size() == 2 && results[0].contains("score") && results[0].contains("confidence"),
          "Manual search pagination exposes the original matching scores");
    index = readJson(root / L"Cache" / L"track-index.json");
    Json manualEntry = {{"titleNormalized", "fixture signal"},
                        {"artistNormalized", "fixture artist"},
                        {"title", "Chosen provider alias"},
                        {"artists", "Different provider artist spelling"},
                        {"songMid", "MANUAL1"},
                        {"songId", "102"},
                        {"matchSource", "Manual"}};
    index["tracks"]["manual-current"] = manualEntry;
    manualEntry["titleNormalized"] = "other track";
    index["tracks"]["manual-other"] = manualEntry;
    saveJson(root / L"Cache" / L"track-index.json", index);
    auto source = root / L"source.lrc";
    {
        std::ofstream f(source);
        f << "[00:01.00]Imported";
    }
    library.importLrc(source, track.key());
    library.importLrc(source, "other|artist|album");
    library.clearManual(&track);
    index = readJson(root / L"Cache" / L"track-index.json");
    check(!index["tracks"].contains("manual-current") && index["tracks"].contains("manual-other") &&
              !fs::exists(importedLyricsPath(root, track.key())),
          "Clearing current manual choice uses input identity, not selected provider aliases");
    library.clearManual();
    index = readJson(root / L"Cache" / L"track-index.json");
    check(index["tracks"].size() == 1 && !index["tracks"].contains("manual-other") && !fs::exists(root / L"Imported"),
          "Clearing all manual choices preserves automatic entries");
}
int main(int argc, char **argv)
{
    try
    {
        auto fixtures = fs::path(__FILE__).parent_path() / L"fixtures";
        auto cases = readJson(fixtures / L"matching-reference.json");
        check(cases.is_array() && cases.size() >= 100, "Reference fixture exists");
        matching::Aliases aliases;
        int mismatch = 0;
        size_t index = 0;
        for (auto &test : cases)
        {
            Snapshot s;
            s.title = test.value("title", "");
            s.artist = test.value("artist", "");
            s.album = test.value("album", "");
            s.duration = test.value("duration", 0) * 1000.;
            auto ranked = matching::rank(s, test["songs"], aliases);
            bool bind = matching::canBind(ranked, s);
            bool same = bind == test.value("canBind", false) &&
                        (!bind || ranked.front().song.value("mid", "") == test.value("best", ""));
            for (auto &c : ranked)
            {
                auto expected = std::find_if(test["songs"].begin(), test["songs"].end(), [&](const auto &song) {
                    return song.value("mid", "") == c.song.value("mid", "");
                });
                if (expected == test["songs"].end() || std::abs(c.score - expected->value("score", 0.)) > .011 ||
                    c.high != expected->value("high", false))
                    same = false;
            }
            if (!same)
            {
                ++mismatch;
                std::cerr << "Parity mismatch fixture " << index << " " << s.title << " bind=" << bind
                          << " expected=" << test.value("canBind", false) << '\n';
                for (auto &c : ranked)
                    std::cerr << "  " << c.song.value("mid", "") << " score=" << c.score << " high=" << c.high << '\n';
            }
            ++index;
        }
        auto qrcFixtures = readJson(fixtures / L"qrc-reference.json");
        check(qrcFixtures.is_array() && qrcFixtures.size() == 12, "QRC reference fixture exists");
        for (auto &test : qrcFixtures)
        {
            auto decoded = qrc::decrypt(test.value("hex", ""));
            check(decoded == qrc::extract(test.value("plain", "")),
                  "Original DES/zlib ciphertext decrypts identically: " + test.value("level", ""));
            check(!qrc::parse(decoded).empty(), "Decrypted QRC parses word timing");
        }
        auto xml = qrc::extract(
            "<Lyric_1 LyricContent=\"[1000,500]A &quot;B&quot;(1000,500)&#10;[2000,1000]C(2000,1000)\" />");
        check(qrc::parse(xml).size() == 2, "XML entities preserve timestamps and lyric quotes");
        auto damaged = qrcFixtures[0].value("hex", "");
        damaged[0] = damaged[0] == '0' ? '1' : '0';
        bool rejected = false;
        try
        {
            qrc::decrypt(damaged);
        }
        catch (const std::exception &)
        {
            rejected = true;
        }
        check(rejected, "Damaged encrypted stream rejected");
        workflows(qrcFixtures);
        check(mismatch == 0, "All original matching reference scores/confidence/binding decisions preserved: " +
                                 std::to_string(mismatch) + " differences");
        std::cout << "Native lyric parity: " << cases.size() << " original matching scenarios and "
                  << qrcFixtures.size() << " DES/zlib vectors passed\n";
        if (argc > 1 && std::string(argv[1]) == "--live")
        {
            wchar_t temp[MAX_PATH]{};
            GetTempPathW(MAX_PATH, temp);
            auto root = fs::path(temp) / (L"TaskbarLyricsNativeLyricsLive-" + std::to_wstring(GetCurrentProcessId()));
            struct Cleanup
            {
                fs::path path;
                ~Cleanup()
                {
                    std::error_code e;
                    fs::remove_all(path, e);
                }
            } cleanup{root};
            Library library(root, root);
            auto results = library.search("BTS Butter");
            check(!results.empty(), "Live search returns results");
            auto doc = library.download(results.front());
            check(!doc.lines.empty(), "Live lyrics download succeeds");
            size_t words = 0, translated = 0;
            for (auto &line : doc.lines)
            {
                words += line.words.size();
                translated += !line.translation.empty();
            }
            std::cout << "Live lyric protocol: lines=" << doc.lines.size() << ", timedWords=" << words
                      << ", translatedLines=" << translated << '\n';
            check(words > 0, "Live QQ QRC supplies real word timestamps");
        }
        return 0;
    }
    catch (const std::exception &e)
    {
        std::cerr << e.what() << '\n';
        return 1;
    }
}
