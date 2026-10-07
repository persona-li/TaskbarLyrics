#pragma once
#include "core.hpp"
#include <filesystem>
#include <fstream>
#include <functional>
#include <memory>
#include <mutex>
#include <thread>
#include <windows.h>
#include <wrl.h>
namespace lyrics
{
namespace fs = std::filesystem;
template <class T> using ComPtr = Microsoft::WRL::ComPtr<T>;
inline std::wstring wide(const std::string &s)
{
    if (s.empty())
        return {};
    int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
    std::wstring r(n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), r.data(), n);
    return r;
}
inline std::string utf8(const std::wstring &s)
{
    if (s.empty())
        return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0, nullptr, nullptr);
    std::string r(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), r.data(), n, nullptr, nullptr);
    return r;
}
inline Json readJson(const fs::path &p)
{
    std::ifstream f(p);
    if (!f)
        return Json::object();
    return Json::parse(f, nullptr, false);
}
inline std::wstring lowerInvariant(const std::wstring &text)
{
    if (text.empty())
        return {};
    int n = LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_LOWERCASE, text.data(), static_cast<int>(text.size()), nullptr,
                          0, nullptr, nullptr, 0);
    if (n <= 0)
        return text;
    std::wstring result(n, L'\0');
    LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_LOWERCASE, text.data(), static_cast<int>(text.size()), result.data(), n,
                  nullptr, nullptr, 0);
    return result;
}
inline void saveJson(const fs::path &p, const Json &j)
{
    fs::create_directories(p.parent_path());
    auto tmp = p;
    tmp += L".tmp";
    {
        std::ofstream f(tmp, std::ios::binary | std::ios::trunc);
        f << j.dump(2);
        f.flush();
        if (!f)
            throw std::runtime_error("Cannot save settings");
    }
    if (!MoveFileExW(tmp.c_str(), p.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH))
        throw std::runtime_error("Cannot replace settings");
}
inline std::string readText(const fs::path &p)
{
    std::ifstream f(p, std::ios::binary);
    std::string bytes{std::istreambuf_iterator<char>(f), {}};
    if (bytes.size() >= 2 &&
        ((static_cast<unsigned char>(bytes[0]) == 0xff && static_cast<unsigned char>(bytes[1]) == 0xfe) ||
         (static_cast<unsigned char>(bytes[0]) == 0xfe && static_cast<unsigned char>(bytes[1]) == 0xff)))
    {
        bool little = static_cast<unsigned char>(bytes[0]) == 0xff;
        std::wstring text;
        for (size_t i = 2; i + 1 < bytes.size(); i += 2)
        {
            auto a = static_cast<unsigned char>(bytes[i]), b = static_cast<unsigned char>(bytes[i + 1]);
            text += static_cast<wchar_t>(little ? (a | (b << 8)) : (b | (a << 8)));
        }
        return utf8(text);
    }
    if (bytes.starts_with("\xEF\xBB\xBF"))
        bytes.erase(0, 3);
    return bytes;
}
inline double ticks()
{
    return static_cast<double>(GetTickCount64());
}
struct Snapshot
{
    std::string title, artist, album, source, error;
    double position{}, duration{}, captured{};
    bool playing{}, previous{}, next{}, toggle{}, seek{};
    bool presentationPlaying{};
    std::string status = "Closed";
    double playbackRate{1};
    std::string key() const
    {
        return title + "|" + artist + "|" + album;
    }
};
class Media
{
    struct Impl;
    std::unique_ptr<Impl> impl;

  public:
    explicit Media(std::function<void(Snapshot)> callback);
    ~Media();
    void command(Json request);
};
struct Document
{
    std::vector<Line> lines;
    std::string origin, error;
    std::string cacheKey;
    bool instrumental{};
};
class Library
{
    fs::path data, legacy;

  public:
    Library(fs::path dataPath, fs::path legacyPath) : data(std::move(dataPath)), legacy(std::move(legacyPath))
    {
    }
    Document load(const Snapshot &track, const Json &settings);
    Document resolve(const Snapshot &track, const Json &settings, std::function<bool()> cancelled = {});
    Json search(const std::string &query, int page = 1, const Snapshot *track = nullptr);
    Document download(const Json &candidate);
    Document importLrc(const fs::path &path, const std::string &key);
    void clearManual(const Snapshot *track = nullptr);
};
class Overlay
{
    struct Impl;
    std::unique_ptr<Impl> impl;

  public:
    Overlay();
    ~Overlay();
    void update(const std::vector<Line> &lines, double position, const Json &settings);
    void place();
    void hide();
    Json layoutState() const;
};
} // namespace lyrics
