#pragma once
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <iomanip>
#include <nlohmann/json.hpp>
#include <regex>
#include <sstream>
#include <string>
#include <vector>
namespace lyrics
{
using Json = nlohmann::json;
struct Word
{
    std::string text;
    double start{}, duration{};
};
struct Line
{
    std::string text, translation;
    double start{}, duration{};
    std::vector<Word> words;
};
inline std::vector<Line> parseLrc(const std::string &source)
{
    std::vector<Line> out;
    std::regex time(R"(\[(\d+):(\d{2})(?:\.(\d{1,3}))?\])"), offset(R"(\[offset:([+-]?\d+)\])");
    std::smatch om;
    double shift = std::regex_search(source, om, offset) ? std::stod(om[1]) : 0;
    std::istringstream input(source);
    std::string row;
    while (std::getline(input, row))
    {
        std::vector<double> times;
        size_t end = 0;
        for (std::sregex_iterator i(row.begin(), row.end(), time), stop; i != stop; ++i)
        {
            auto f = (*i)[3].str();
            double ms = f.empty() ? 0 : std::stod(f) * std::pow(10, 3 - static_cast<int>(f.size()));
            times.push_back(std::stod((*i)[1]) * 60000 + std::stod((*i)[2]) * 1000 + ms + shift);
            end = static_cast<size_t>(i->position() + i->length());
        }
        auto text = row.substr(end);
        if (!text.empty() && text.back() == '\r')
            text.pop_back();
        for (auto t : times)
            out.push_back({text, "", t, 0, {}});
    }
    std::stable_sort(out.begin(), out.end(), [](auto &a, auto &b) { return a.start < b.start; });
    for (size_t i = 0; i < out.size(); ++i)
        out[i].duration = i + 1 < out.size() ? std::max(0., out[i + 1].start - out[i].start) : 5000;
    return out;
}
inline int currentLine(const std::vector<Line> &lines, double position)
{
    auto i =
        std::upper_bound(lines.begin(), lines.end(), position, [](double p, const Line &l) { return p < l.start; });
    return i == lines.begin() ? -1 : static_cast<int>(i - lines.begin() - 1);
}
inline double progress(const Line &line, double position)
{
    if (line.words.empty())
        return line.duration > 0 ? std::clamp((position - line.start) / line.duration, 0., 1.) : 0.;
    double length = 0, done = 0;
    for (auto &w : line.words)
    {
        auto n = static_cast<double>(w.text.size());
        length += n;
        done += n * std::clamp((position - w.start) / std::max(1., w.duration), 0., 1.);
    }
    return length ? done / length : 0;
}
inline int resolvedLine(const std::vector<Line> &lines, double position)
{
    int index = currentLine(lines, std::max(0., position));
    if (index < 0)
        return -1;
    auto end = lines[index].start + lines[index].duration;
    if (position <= end || lines[index].duration <= 0)
        return index;
    bool shortGap = index + 1 < static_cast<int>(lines.size()) && lines[index + 1].start - end <= 2000;
    return shortGap || position - end <= 1500 ? index : -1;
}
struct Interval
{
    int left{}, right{};
    int width() const
    {
        return std::max(0, right - left);
    }
};
inline Interval gap(int left, int right, std::vector<Interval> occupied, int inset = 8, int safety = 20)
{
    std::sort(occupied.begin(), occupied.end(), [](auto a, auto b) { return a.left < b.left; });
    Interval best{left, left};
    int cursor = left;
    auto consider = [&](int a, int b) {
        a = std::min(b, a + inset);
        b = std::max(a, b - safety);
        if (b - a > best.width())
            best = {a, b};
    };
    for (auto x : occupied)
    {
        if (x.right <= left || x.left >= right)
            continue;
        consider(cursor, std::clamp(x.left, left, right));
        cursor = std::max(cursor, std::min(right, x.right));
    }
    consider(cursor, right);
    return best;
}
struct Placement
{
    int left{}, width{};
    bool right{};
};
inline Placement choose(Interval left, Interval right, int minimum, int maximum)
{
    if (left.width() >= minimum)
        return {left.left, std::min(left.width(), maximum), false};
    if (right.width() >= minimum)
    {
        int w = std::min(right.width(), maximum);
        return {right.right - w, w, true};
    }
    return {}; // Measured lack of space must never fall back to an overlapping rectangle.
}
inline double hue(double h)
{
    return std::isfinite(h) ? std::fmod(std::fmod(h, 360.) + 360., 360.) : 264.540965113;
}
inline std::array<double, 3> linear(double l, double c, double h)
{
    double a = c * std::cos(h * 3.141592653589793 / 180), b = c * std::sin(h * 3.141592653589793 / 180);
    double x = l + .3963377774 * a + .2158037573 * b, y = l - .1055613458 * a - .0638541728 * b,
           z = l - .0894841775 * a - 1.2914855480 * b;
    x = x * x * x;
    y = y * y * y;
    z = z * z * z;
    return {4.0767416621 * x - 3.3077115913 * y + .2309699292 * z,
            -1.2684380046 * x + 2.6097574011 * y - .3413193965 * z,
            -.0041960863 * x - .7034186147 * y + 1.707614701 * z};
}
inline std::string color(double l, double c, double h)
{
    h = hue(h);
    auto rgb = linear(l, c, h);
    auto valid = [](auto v) {
        return std::all_of(v.begin(), v.end(), [](double x) { return x >= -1e-12 && x <= 1 + 1e-12; });
    };
    if (!valid(rgb))
    {
        double low = 0, high = c;
        rgb = linear(l, 0, h);
        for (int i = 0; i < 48; ++i)
        {
            double m = (low + high) / 2;
            auto v = linear(l, m, h);
            if (valid(v))
            {
                low = m;
                rgb = v;
            }
            else
                high = m;
        }
    }
    std::ostringstream s;
    s << "#FF" << std::uppercase << std::hex << std::setfill('0');
    for (double x : rgb)
    {
        x = std::clamp(x, 0., 1.);
        double y = x <= .0031308 ? 12.92 * x : 1.055 * std::pow(x, 1 / 2.4) - .055;
        s << std::setw(2) << static_cast<int>(std::round(y * 255));
    }
    return s.str();
}
inline std::pair<std::string, std::string> palette(double h)
{
    return {color(.548562815, .134419357, h), color(.825590585, .066768327, h - 22.733561535)};
}
inline Json defaultSettings()
{
    return {{"font", "Microsoft YaHei UI"},
            {"fontSize", 14.},
            {"normal", "#FF496DBF"},
            {"highlight", "#FFA0CCEE"},
            {"hue", 264.540965113},
            {"shadow", true},
            {"shadowColor", "#80000000"},
            {"shadowOffsetX", 1.},
            {"shadowOffsetY", 1.},
            {"alignment", "Center"},
            {"karaoke", true},
            {"autoPan", true},
            {"fonts",
             {{"chinese", "Microsoft YaHei UI"},
              {"japanese", "Microsoft YaHei UI"},
              {"korean", "Microsoft YaHei UI"},
              {"latin", "Microsoft YaHei UI"},
              {"cyrillic", "Microsoft YaHei UI"},
              {"arabic", "Microsoft YaHei UI"},
              {"other", "Microsoft YaHei UI"}}},
            {"autoLayout", true},
            {"verticalOffset", 0.},
            {"leftMargin", 8},
            {"rightMargin", 40},
            {"minWidth", 750},
            {"maxWidth", 1350},
            {"fallbackWidthPercent", 60.},
            {"startup", false},
            {"animations", true},
            {"recentColors", {{"normal", Json::array()}, {"highlight", Json::array()}, {"shadow", Json::array()}}},
            {"overlay", true},
            {"theme", "system"},
            {"globalOffset", 0},
            {"trackOffsets", Json::object()},
            {"manual", Json::object()}};
}
inline bool accepts(uint64_t generation, const std::string &key, uint64_t resultGeneration,
                    const std::string &resultKey)
{
    return generation == resultGeneration && key == resultKey;
}
} // namespace lyrics
