#pragma once
#include "core.hpp"
#include "lyrics_des.hpp"
#include "lyrics_inflate.hpp"
#include <cctype>
#include <charconv>
#include <cstring>
#include <regex>
#include <sstream>
namespace lyrics::qrc
{
inline std::string entities(std::string text)
{
    const std::pair<const char *, const char *> substitutions[] = {{"&lt;", "<"},   {"&gt;", ">"},   {"&quot;", "\""},
                                                                   {"&apos;", "'"}, {"&#13;", "\r"}, {"&#10;", "\n"},
                                                                   {"&#xD;", "\r"}, {"&#xA;", "\n"}, {"&amp;", "&"}};
    for (auto [from, to] : substitutions)
    {
        size_t pos = 0;
        while ((pos = text.find(from, pos)) != std::string::npos)
        {
            text.replace(pos, strlen(from), to);
            pos += strlen(to);
        }
    }
    return text;
}
inline std::string node(const std::string &xml, const std::string &name)
{
    std::smatch match;
    std::regex pattern("<" + name + "(?:\\s[^>]*)?>([\\s\\S]*?)</" + name + "\\s*>", std::regex::icase);
    if (!std::regex_search(xml, match, pattern))
        return {};
    auto text = match[1].str();
    if (text.starts_with("<![CDATA[") && text.ends_with("]]>"))
        text = text.substr(9, text.size() - 12);
    return entities(text);
}
inline std::string extract(const std::string &text)
{
    if (text.find("LyricContent") == std::string::npos)
        return text;
    // QQ XML may contain unescaped quotes within the value; terminate at the next attribute or element terminator.
    std::smatch match;
    static const std::regex attribute(R"re(LyricContent\s*=\s*"([\s\S]*?)"(?=\s+[\w:.-]+\s*=|\s*/?>))re",
                                      std::regex::icase);
    if (std::regex_search(text, match, attribute))
        return entities(match[1].str());
    auto lyric = node(text, "Lyric_1");
    return lyric.empty() ? text : lyric;
}
inline std::string decrypt(const std::string &payload)
{
    std::string compact;
    for (unsigned char c : payload)
        if (!std::isspace(c))
            compact += c;
    bool hex = compact.size() >= 16 && compact.size() % 2 == 0 &&
               compact.find_first_not_of("0123456789abcdefABCDEF") == std::string::npos;
    if (!hex)
        return extract(payload);
    if (compact.size() > 16 * 1024 * 1024 || compact.size() % 16)
        throw std::runtime_error("Invalid QRC block size");
    std::vector<uint8_t> bytes;
    bytes.reserve(compact.size() / 2);
    for (size_t i = 0; i < compact.size(); i += 2)
        bytes.push_back(static_cast<uint8_t>(std::stoi(compact.substr(i, 2), nullptr, 16)));
    constexpr uint8_t key[] = "!@#)(*$%123ZXC!@!@#)(NHL";
    Des::Schedule schedule{};
    Des::TripleDESKeySetup(key, schedule, Des::DECRYPT);
    for (size_t i = 0; i < bytes.size(); i += 8)
    {
        uint8_t output[8]{};
        Des::TripleDESCrypt(bytes.data() + i, output, schedule);
        std::copy(output, output + 8, bytes.begin() + i);
    }
    std::string plain;
    try
    {
        plain = inflate(bytes);
    }
    catch (const std::exception &)
    {
        plain = inflate(bytes, false);
    }
    if (plain.starts_with("\xEF\xBB\xBF"))
        plain.erase(0, 3);
    return extract(plain);
}
inline std::vector<Line> parse(const std::string &payload)
{
    std::vector<Line> lines;
    static const std::regex header(R"(^\s*\[(\d+)\s*,\s*(\d+)\](.*)$)"), word(R"((.*?)\((\d+)\s*,\s*(\d+)\))");
    std::istringstream stream(extract(payload));
    std::string raw;
    while (std::getline(stream, raw))
    {
        if (!raw.empty() && raw.back() == '\r')
            raw.pop_back();
        std::smatch m;
        bool timed = std::regex_match(raw, m, header);
        Line line{"", "", timed ? std::stod(m[1]) : 0, timed ? std::stod(m[2]) : 0, {}};
        auto body = timed ? m[3].str() : raw;
        if (!timed && body.starts_with('['))
        {
            auto bracket = body.find(']');
            if (bracket != std::string::npos)
                body.erase(0, bracket + 1);
        }
        if (!timed && raw.find(':') != std::string::npos && raw.starts_with('['))
            continue;
        for (std::sregex_iterator it(body.begin(), body.end(), word), finish; it != finish; ++it)
        {
            auto &w = *it;
            line.words.push_back({w[1].str(), std::stod(w[2]), std::stod(w[3])});
            line.text += w[1].str();
        }
        if (line.words.empty())
        {
            if (!timed)
                continue;
            line.text = body;
        }
        else
        {
            if (line.start <= 0)
                line.start = line.words.front().start;
            if (line.duration <= 0)
                line.duration =
                    std::max(0., line.words.back().start + line.words.back().duration - line.words.front().start);
        }
        if (!line.text.empty())
            lines.push_back(std::move(line));
    }
    std::stable_sort(lines.begin(), lines.end(), [](const auto &a, const auto &b) { return a.start < b.start; });
    return lines;
}
} // namespace lyrics::qrc
