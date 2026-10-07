#pragma once
#include "platform.hpp"
#include <tuple>
namespace lyrics
{
// Import values without altering the WPF file or replacing a user's font selection.
inline Json migrateSettings(const Json &old)
{
    auto out = defaultSettings();
    if (!old.is_object())
        return out;
    if (!old.contains("display"))
    {
        out.update(old);
        return out;
    }
    const auto d = old.value("display", Json::object());
    const auto o = old.value("overlay", Json::object());
    const auto k = old.value("karaoke", Json::object());
    const auto g = old.value("general", Json::object());
    const auto l = old.value("lyrics", Json::object());
    auto copy = [&](const Json &from, const char *source, const char *target) {
        if (from.is_object() && from.contains(source))
            out[target] = from[source];
    };
    for (auto [a, b] : {std::pair{"fontSize", "fontSize"},
                        {"normalColor", "normal"},
                        {"highlightColor", "highlight"},
                        {"paletteHue", "hue"},
                        {"shadowEnabled", "shadow"},
                        {"shadowColor", "shadowColor"},
                        {"shadowOffsetX", "shadowOffsetX"},
                        {"shadowOffsetY", "shadowOffsetY"},
                        {"textAlignment", "alignment"},
                        {"verticalOffsetPx", "verticalOffset"}})
        copy(d, a, b);
    if (d.contains("fonts") && d["fonts"].is_object())
    {
        out["fonts"].update(d["fonts"]);
        out["font"] = out["fonts"].value("chinese", "Microsoft YaHei UI");
    }
    for (auto [a, b] : {std::pair{"autoFit", "autoLayout"},
                        {"leftMarginPx", "leftMargin"},
                        {"rightSafetyMarginPx", "rightMargin"},
                        {"minWidthPx", "minWidth"},
                        {"maxWidthPx", "maxWidth"}})
        copy(o, a, b);
    if (o.contains("widthRatio") && o["widthRatio"].is_number())
        out["fallbackWidthPercent"] = o["widthRatio"].get<double>() * 100.;
    copy(k, "enabled", "karaoke");
    copy(k, "autoPanLongLyrics", "autoPan");
    copy(g, "showOverlay", "overlay");
    copy(g, "startWithWindows", "startup");
    if (g.contains("reduceMotion") && g["reduceMotion"].is_boolean())
        out["animations"] = !g["reduceMotion"].get<bool>();
    auto theme = g.value("theme", "FollowSystem");
    out["theme"] = theme == "Dark" ? "dark" : theme == "Light" ? "light" : "system";
    copy(l, "globalOffsetMs", "globalOffset");
    for (auto [a, b] : {std::pair{"recentNormalColors", "normal"},
                        {"recentHighlightColors", "highlight"},
                        {"recentShadowColors", "shadow"}})
        if (g.contains(a) && g[a].is_array())
            out["recentColors"][b] = g[a];
    return out;
}
inline void normalizeSettings(Json &s)
{
    auto defaults = defaultSettings();
    for (auto &x : defaults.items())
        if (!s.contains(x.key()) ||
            (s[x.key()].type() != x.value().type() && !(s[x.key()].is_number() && x.value().is_number())))
            s[x.key()] = x.value();
    for (auto [key, min, max] : {std::tuple{"fontSize", 10., 72.},
                                 {"verticalOffset", -40., 40.},
                                 {"leftMargin", 0., 300.},
                                 {"rightMargin", 0., 300.},
                                 {"minWidth", 0., 3000.},
                                 {"maxWidth", 100., 5000.},
                                 {"fallbackWidthPercent", 5., 69.},
                                 {"globalOffset", -5000., 5000.},
                                 {"shadowOffsetX", -10., 10.},
                                 {"shadowOffsetY", -10., 10.}})
    {
        double v = s[key].get<double>();
        if (!std::isfinite(v))
            v = defaults[key].get<double>();
        if (defaults[key].is_number_integer())
            s[key] = static_cast<int>(std::clamp(v, min, max));
        else
            s[key] = std::clamp(v, min, max);
    }
    for (auto &v : s["trackOffsets"])
        v = v.is_number() ? Json(std::clamp(v.get<int>(), -5000, 5000)) : Json(0);
    for (auto key : {"normal", "highlight", "shadowColor"})
        if (!std::regex_match(s[key].get<std::string>(), std::regex("#[0-9A-Fa-f]{8}")))
            s[key] = defaults[key];
    for (auto &x : defaults["fonts"].items())
        if (!s["fonts"].contains(x.key()) || !s["fonts"][x.key()].is_string() ||
            s["fonts"][x.key()].get<std::string>().size() > 256)
            s["fonts"][x.key()] = x.value();
    for (auto key : {"normal", "highlight", "shadow"})
    {
        auto &recent = s["recentColors"][key];
        if (!recent.is_array())
            recent = Json::array();
        Json valid = Json::array();
        for (auto &v : recent)
            if (v.is_string() && std::regex_match(v.get<std::string>(), std::regex("#[0-9A-Fa-f]{8}")) &&
                std::find(valid.begin(), valid.end(), v) == valid.end() && valid.size() < 6)
                valid.push_back(v);
        recent = valid;
    }
    if (s["theme"] != "light" && s["theme"] != "dark" && s["theme"] != "system")
        s["theme"] = "system";
    if (s["alignment"] != "Left" && s["alignment"] != "Center" && s["alignment"] != "Right")
        s["alignment"] = "Center";
    auto h = s["hue"].get<double>();
    s["hue"] = h == -1 ? -1 : hue(std::isfinite(h) ? h : 264.540965113);
}
inline bool systemLight(const wchar_t *name = L"AppsUseLightTheme")
{
    DWORD value = 1, size = sizeof(value);
    RegGetValueW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize", name,
                 RRF_RT_REG_DWORD, nullptr, &value, &size);
    return value != 0;
}
inline void setStartup(const fs::path &executable, bool enabled)
{
    HKEY key{};
    if (RegCreateKeyExW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Run", 0, nullptr, 0,
                        KEY_SET_VALUE, nullptr, &key, nullptr) != ERROR_SUCCESS)
        throw std::runtime_error("无法修改开机启动项");
    LONG result = ERROR_SUCCESS;
    if (enabled)
    {
        auto value = L"\"" + executable.wstring() + L"\" --background";
        result = RegSetValueExW(key, L"TaskbarLyrics.Native", 0, REG_SZ, reinterpret_cast<const BYTE *>(value.c_str()),
                                static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t)));
    }
    else
    {
        result = RegDeleteValueW(key, L"TaskbarLyrics.Native");
        if (result == ERROR_FILE_NOT_FOUND)
            result = ERROR_SUCCESS;
    }
    RegCloseKey(key);
    if (result != ERROR_SUCCESS)
        throw std::runtime_error("无法修改开机启动项");
}
} // namespace lyrics
