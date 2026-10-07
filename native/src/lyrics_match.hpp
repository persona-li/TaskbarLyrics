#pragma once
#include "platform.hpp"
#include <cwctype>
#include <map>
#include <numeric>
#include <set>
#pragma comment(lib, "normaliz.lib")
namespace lyrics::matching
{
inline std::wstring trim(std::wstring s)
{
    auto begin = s.find_first_not_of(L" \t\r\n\u3000\u00a0"), end = s.find_last_not_of(L" \t\r\n\u3000\u00a0");
    return begin == std::wstring::npos ? L"" : s.substr(begin, end - begin + 1);
}
inline std::wstring collapse(std::wstring s)
{
    return trim(std::regex_replace(s, std::wregex(L"\\s+"), L" "));
}
inline std::wstring half(std::wstring text)
{
    int n = NormalizeString(NormalizationKC, text.data(), static_cast<int>(text.size()), nullptr, 0);
    if (n > 0)
    {
        std::wstring value(n, 0);
        n = NormalizeString(NormalizationKC, text.data(), static_cast<int>(text.size()), value.data(), n);
        if (n > 0)
        {
            value.resize(n);
            text = std::move(value);
        }
    }
    return lowerInvariant(text);
}
inline std::wstring normalize(std::wstring s, bool artist = false)
{
    s = half(trim(s));
    for (auto &c : s)
    {
        if (c == L'\u3000' || c == L'\u00a0')
            c = L' ';
        if (c == L'【')
            c = L'[';
        if (c == L'】')
            c = L']';
        if (c == L'「' || c == L'」' || c == L'『' || c == L'』')
            c = L'"';
        if (c == L'—' || c == L'–')
            c = L'-';
        if (artist && (c == L'&' || c == L',' || c == L'，' || c == L'、'))
            c = L'/';
    }
    s = std::regex_replace(s, std::wregex(L"\\b(feat\\.?|ft\\.?|featuring)\\b", std::regex::icase),
                           artist ? L"/" : L" ");
    return collapse(s);
}
inline std::wstring simplified(std::wstring s)
{
    s = half(s);
    for (auto &c : s)
        if (std::wstring(L"=()【】[]「」『』:;,.!?/_-—–。").find(c) != std::wstring::npos)
            c = L' ';
    return collapse(s);
}
inline std::wstring stripped(std::wstring s)
{
    return collapse(std::regex_replace(s, std::wregex(L"\\s*[\\(\\[][^\\(\\)\\[\\]]+[\\)\\]]"), L" "));
}
inline std::wstring duplicateAlbum(std::wstring s)
{
    while (!s.empty() && (s.back() == L')' || s.back() == L']'))
    {
        std::vector<wchar_t> stack;
        size_t opening = 0;
        for (size_t i = s.size(); i-- > 0;)
        {
            wchar_t c = s[i];
            if (c == L')' || c == L']')
                stack.push_back(c);
            else if (c == L'(' || c == L'[')
            {
                if (stack.empty() || stack.back() != (c == L'(' ? L')' : L']'))
                    return s;
                stack.pop_back();
                if (stack.empty())
                {
                    opening = i;
                    break;
                }
            }
        }
        if (!opening)
            return s;
        auto core = trim(s.substr(0, opening)), annotation = trim(s.substr(opening + 1, s.size() - opening - 2));
        if (core != annotation)
            return s;
        s = core;
    }
    return s;
}
inline std::set<std::wstring> artists(std::wstring s, bool removeAnnotations = false)
{
    s = normalize(s, true);
    std::set<std::wstring> out;
    size_t start = 0;
    do
    {
        size_t end = s.find_first_of(L"/|;", start);
        auto value = trim(s.substr(start, end == std::wstring::npos ? end : end - start));
        if (removeAnnotations)
            value = stripped(value);
        if (!value.empty())
            out.insert(value);
        if (end == std::wstring::npos)
            break;
        start = end + 1;
    } while (start < s.size());
    return out;
}
inline bool subset(const std::set<std::wstring> &a, const std::set<std::wstring> &b)
{
    return std::includes(b.begin(), b.end(), a.begin(), a.end());
}
inline double similarity(const std::wstring &a, const std::wstring &b)
{
    if (a.empty() && b.empty())
        return 1;
    if (a.empty() || b.empty())
        return 0;
    std::vector<size_t> previous(b.size() + 1), current(b.size() + 1);
    std::iota(previous.begin(), previous.end(), 0);
    for (size_t i = 1; i <= a.size(); ++i)
    {
        current[0] = i;
        for (size_t j = 1; j <= b.size(); ++j)
            current[j] = std::min({current[j - 1] + 1, previous[j] + 1, previous[j - 1] + (a[i - 1] != b[j - 1])});
        previous.swap(current);
    }
    return 1. - double(previous.back()) / std::max(a.size(), b.size());
}
inline std::vector<std::wstring> groups(const std::wstring &text)
{
    static const std::wregex pattern(L"[\\(\\[]([^\\(\\)\\[\\]]+)[\\)\\]]");
    std::vector<std::wstring> out;
    for (std::wsregex_iterator i(text.begin(), text.end(), pattern), end; i != end; ++i)
        out.push_back((*i)[1]);
    return out;
}
inline bool declaredAlias(const std::wstring &title, const std::wstring &candidate)
{
    auto c = simplified(candidate);
    if (c.empty())
        return false;
    for (auto &group : groups(half(title)))
        if (simplified(group) == c)
            return true;
    return false;
}
inline std::wstring parts(const std::wstring &s)
{
    auto values = groups(s);
    for (auto &v : values)
        v = simplified(v);
    auto core = simplified(std::regex_replace(s, std::wregex(L"[\\(\\[]([^\\(\\)\\[\\]]+)[\\)\\]]"), L" "));
    if (!core.empty())
        values.push_back(core);
    values.erase(std::remove(values.begin(), values.end(), L""), values.end());
    if (values.size() < 2)
        return {};
    std::sort(values.begin(), values.end());
    std::wstring result;
    for (auto &v : values)
        result += v + L'\x1f';
    return result;
}
inline double titleScore(const std::string &query, const std::string &candidate)
{
    auto q = half(trim(wide(query))), c = half(trim(wide(candidate)));
    if (q.empty() || c.empty())
        return q == c ? 100 : 0;
    if (q == c)
        return 100;
    auto nq = normalize(q), nc = normalize(c);
    if (nq == nc)
        return 98;
    auto pq = parts(nq), pc = parts(nc);
    if (!pq.empty() && pq == pc)
        return 97;
    auto sq = simplified(q), sc = simplified(c);
    if (!sq.empty() && sq == sc)
        return 96;
    if (declaredAlias(q, c) || declaredAlias(c, q))
        return 97;
    auto cq = trim(std::regex_replace(nq, std::wregex(L"\\s*[\\(\\[\\{]\\s*[\\)\\]\\}]\\s*$"), L""));
    auto cc = trim(std::regex_replace(nc, std::wregex(L"\\s*[\\(\\[\\{]\\s*[\\)\\]\\}]\\s*$"), L""));
    if (!cq.empty() && cq == cc)
        return 90;
    if (cq.size() >= 2 && !cc.empty() && (nc.find(cq) != std::wstring::npos || nq.find(cc) != std::wstring::npos))
        return 85;
    return std::max({similarity(nq, nc), similarity(sq, sc), similarity(cq, cc) * .95}) * 100;
}
inline double albumScore(const std::string &query, const std::string &candidate)
{
    auto q = half(trim(wide(query))), c = half(trim(wide(candidate)));
    if (q.empty() || c.empty())
        return 0;
    if (q == c)
        return 100;
    q = normalize(q);
    c = normalize(c);
    if (q == c || duplicateAlbum(q) == duplicateAlbum(c))
        return 98;
    if (q.find(c) != std::wstring::npos || c.find(q) != std::wstring::npos)
        return 85;
    return similarity(q, c) * 100;
}
struct Aliases
{
    std::vector<std::set<std::wstring>> sets;
    Aliases();
    void add(const std::vector<std::string> &names)
    {
        std::set<std::wstring> group;
        for (auto &n : names)
            group.insert(normalize(wide(n), true));
        if (!group.empty())
            sets.push_back(std::move(group));
    }
    void load(const Json &json)
    {
        const Json &map = json.contains("aliases") && json["aliases"].is_object() ? json["aliases"] : json;
        if (!map.is_object())
            return;
        for (auto i = map.begin(); i != map.end(); ++i)
        {
            if (!i.value().is_array())
                continue;
            std::vector<std::string> group{i.key()};
            for (auto &v : i.value())
                if (v.is_string())
                    group.push_back(v.get<std::string>());
            add(group);
        }
    }
    bool same(const std::wstring &a, const std::wstring &b) const
    {
        for (auto &set : sets)
            if (set.contains(a) && set.contains(b))
                return true;
        return a == b;
    }
    std::vector<std::string> names(const std::string &artist) const
    {
        auto key = normalize(wide(artist), true);
        std::set<std::wstring> result;
        for (auto &set : sets)
            if (set.contains(key))
                result.insert(set.begin(), set.end());
        result.erase(key);
        std::vector<std::string> out;
        for (auto &v : result)
            out.push_back(utf8(v));
        return out;
    }
};
inline double artistScore(const std::string &query, const std::string &candidate, const Aliases &aliases)
{
    auto qs = artists(wide(query)), cs = artists(wide(candidate));
    if (qs.empty() || cs.empty())
        return qs == cs ? 100 : 0;
    double best = 0;
    for (auto &q : qs)
        for (auto &c : cs)
        {
            if (q == c)
                best = 100;
            else if (aliases.same(q, c))
                best = std::max(best, 95.);
            else if (stripped(q).size() >= 2 && stripped(q) == stripped(c))
                best = std::max(best, 98.);
            else if (q.find(c) != std::wstring::npos || c.find(q) != std::wstring::npos)
            {
                if (std::min(q.size(), c.size()) >= 2)
                    best = std::max(best, 88.);
            }
            else
                best = std::max(best, similarity(q, c) * 100);
        }
    return best;
}
inline double durationScore(int q, int c)
{
    if (q <= 0 || c <= 0)
        return 0;
    int diff = std::abs(q - c);
    return diff <= 2 ? 100 : diff <= 5 ? 90 : diff <= 10 ? 70 : diff <= 15 ? 40 : diff <= 20 ? 20 : 0;
}
enum Version : uint32_t
{
    Live = 1,
    Japanese = 2,
    Chinese = 4,
    Korean = 8,
    English = 16,
    Remix = 32,
    Cover = 64,
    Instrumental = 128,
    Acoustic = 256,
    Remaster = 512,
    SpedUp = 1024,
    Slowed = 2048,
    Demo = 4096,
    ReRecorded = 8192
};
constexpr uint32_t Languages = Japanese | Chinese | Korean | English;
inline uint32_t versions(const std::string &title, const std::string &album = {})
{
    static const std::vector<std::pair<uint32_t, std::wregex>> rules = {
        {Live, std::wregex(L"\\b(live|concert)\\b|现场|ライブ|演唱會|演唱会", std::regex::icase)},
        {Japanese,
         std::wregex(L"\\b(japanese\\s*ver(?:sion)?|japan\\s*edition|jp\\s*ver)\\b|日文版|日本語|日本版|日语版",
                     std::regex::icase)},
        {Chinese, std::wregex(L"\\b(chinese\\s*ver(?:sion)?|cn\\s*ver|mandarin)\\b|中文版|国语版|國語版|华语版",
                              std::regex::icase)},
        {Korean, std::wregex(L"\\b(korean\\s*ver(?:sion)?|kr\\s*ver)\\b|韩文版|韩语版|韓文版|韓語版|한국어\\s*버전",
                             std::regex::icase)},
        {English, std::wregex(L"\\b(english\\s*ver(?:sion)?|en\\s*ver)\\b|英文版|英语版", std::regex::icase)},
        {Remix, std::wregex(L"\\b(remix|dj|mix|mashup)\\b|混音|网友改编|網友改編", std::regex::icase)},
        {Cover, std::wregex(L"\\bcover\\b|翻唱|カバー|カバー曲", std::regex::icase)},
        {Instrumental, std::wregex(L"\\b(instrumental|inst\\.?|karaoke|off\\s*vocal)\\b|伴奏|纯音乐|純音樂|カラオケ",
                                   std::regex::icase)},
        {Acoustic, std::wregex(L"\\b(acoustic|unplugged)\\b|原声|不插电|不插電", std::regex::icase)},
        {Remaster, std::wregex(L"\\b(remaster(?:ed)?)\\b|重制|重製|数字修复", std::regex::icase)},
        {SpedUp, std::wregex(L"\\b(sped\\s*up|speed\\s*up|nightcore)\\b|加速", std::regex::icase)},
        {Slowed, std::wregex(L"\\b(slowed|slowed\\s*\\+?\\s*reverb)\\b|减速|慢速", std::regex::icase)},
        {Demo, std::wregex(L"\\bdemo\\b|小样|小樣", std::regex::icase)},
        {ReRecorded,
         std::wregex(L"\\b(re[- ]?recorded|taylor'?s\\s*version)\\b|重新录制|重新錄製|重录", std::regex::icase)}};
    uint32_t flags = 0;
    auto text = wide(title + " " + album);
    for (auto &[flag, pattern] : rules)
        if (std::regex_search(text, pattern))
            flags |= flag;
    return flags;
}
inline double penalty(uint32_t flags)
{
    const std::pair<uint32_t, int> values[] = {
        {Live, 25},         {Japanese, 30}, {Chinese, 30},  {Korean, 30}, {English, 30}, {Remix, 35}, {Cover, 40},
        {Instrumental, 35}, {Acoustic, 15}, {Remaster, 10}, {SpedUp, 40}, {Slowed, 40},  {Demo, 25},  {ReRecorded, 25}};
    double result = 0;
    for (auto [flag, value] : values)
        if (flags & flag)
            result += value;
    return result;
}
struct Candidate
{
    Json song;
    double title{}, artist{}, duration{}, album{}, penalty{}, score{};
    uint32_t flags{};
    bool high{};
    int seconds{}, rank{};
};
inline bool exact(const Candidate &c)
{
    return c.title >= 98 && c.artist >= 95 && c.album >= 95 && c.penalty == 0;
}
inline bool strong(const Candidate &c)
{
    return c.score >= 80 && c.title >= 97 && c.artist >= 95 &&
           ((c.album >= 80 && (c.duration >= 20 || exact(c))) || c.artist >= 98) && c.penalty == 0;
}
inline bool equivalent(const Candidate &a, const Candidate &b)
{
    if (a.flags != b.flags || a.seconds <= 0 || b.seconds <= 0 || std::abs(a.seconds - b.seconds) > 2)
        return false;
    auto aa = stripped(normalize(wide(a.song.value("artist", "")), true)),
         ba = stripped(normalize(wide(b.song.value("artist", "")), true));
    auto al = duplicateAlbum(normalize(wide(a.song.value("album", "")))),
         bl = duplicateAlbum(normalize(wide(b.song.value("album", ""))));
    if (aa.empty() || aa != ba || al.empty() || al != bl)
        return false;
    auto at = a.song.value("title", ""), bt = b.song.value("title", "");
    return normalize(wide(at)) == normalize(wide(bt)) || declaredAlias(wide(at), wide(bt)) ||
           declaredAlias(wide(bt), wide(at));
}
inline Candidate score(const Snapshot &track, const Json &song, const Aliases &aliases, int rank)
{
    Candidate c;
    c.song = song;
    c.rank = rank;
    c.seconds = song.value("duration", 0);
    c.title = titleScore(track.title, song.value("title", ""));
    c.artist = track.artist.empty() ? 0 : artistScore(track.artist, song.value("artist", ""), aliases);
    int seconds = static_cast<int>(std::round(track.duration / 1000));
    c.duration = durationScore(seconds, c.seconds);
    c.album = albumScore(track.album, song.value("album", ""));
    bool ha = !track.artist.empty(), hd = seconds > 0, useDuration = hd && std::abs(seconds - c.seconds) <= 20;
    bool hal = !track.album.empty() && !song.value("album", "").empty();
    double wt = 1, wa = 0, wd = 0, wal = 0;
    if (hal && ha && useDuration)
    {
        wt = .4;
        wa = .3;
        wd = .2;
        wal = .1;
    }
    else if (hal && ha)
    {
        wt = .5;
        wa = .35;
        wal = .15;
    }
    else if (ha && useDuration)
    {
        wt = .45;
        wa = .35;
        wd = .2;
    }
    else if (ha)
    {
        wt = .55;
        wa = .45;
    }
    else if (hd)
    {
        wt = .8;
        wd = .2;
    }
    uint32_t input = versions(track.title, track.album);
    c.flags = versions(song.value("title", ""), song.value("album", ""));
    c.penalty = penalty((c.flags & ~input) | ((c.flags ^ input) & Languages));
    c.score = c.title * wt + c.artist * wa + c.duration * wd + c.album * wal;
    if (input & c.flags)
        c.score = std::min(100., c.score + 3);
    c.score = std::clamp(c.score - c.penalty, 0., 100.);
    if (c.title < 45)
        c.score = std::min(c.score, 50.);
    if (ha && c.artist < 40)
        c.score = std::min(c.score, 60.);
    if (hd && std::abs(seconds - c.seconds) > 30 && !exact(c))
        c.score = std::min(c.score, 70.);
    uint32_t extra = c.flags & ~input;
    bool blocked = ((c.flags ^ input) & Languages) ||
                   (extra & (Cover | Remix | Japanese | Chinese | English | Instrumental | SpedUp | Slowed));
    c.high = !blocked && (!ha || c.artist >= 50) && c.title >= 50 &&
             !(hd && std::abs(seconds - c.seconds) > 30 && !exact(c)) && (strong(c) || c.score >= 90);
    if (hal && c.album < 50)
        c.high = false;
    c.score = std::round(c.score * 100) / 100;
    return c;
}
inline std::pair<std::wstring, std::set<std::wstring>> trackParts(const std::string &title)
{
    auto text = half(wide(title));
    std::set<std::wstring> featured;
    static const std::wregex group(L"[\\(\\[]([^\\(\\)\\[\\]]+)[\\)\\]]"),
        language(L"^(?:(?:english|japanese|chinese|korean|en|jp|cn|kr)\\s*ver(?:sion)?\\.?|英文版|英语版|日文版|日语版|"
                 L"日本語|日本版|中文版|国语版|國語版|华语版|韩文版|韩语版|韓文版|韓語版|한국어\\s*버전)$",
                 std::regex::icase),
        credit(L"^(?:feat(?:uring)?|ft)\\.?\\s+(.+)$", std::regex::icase);
    std::wstring core;
    size_t previous = 0;
    for (std::wsregex_iterator it(text.begin(), text.end(), group), end; it != end; ++it)
    {
        auto &m = *it;
        core += text.substr(previous, m.position() - previous);
        auto annotation = trim(m[1]);
        std::wsmatch cm;
        if (std::regex_match(annotation, cm, credit))
        {
            auto names = artists(cm[1], true);
            featured.insert(names.begin(), names.end());
            core += L' ';
        }
        else if (std::regex_match(annotation, language))
            core += L' ';
        else
            core += m.str();
        previous = m.position() + m.length();
    }
    core += text.substr(previous);
    return {normalize(core), featured};
}
inline std::wstring withoutFeatured(const std::string &title)
{
    auto text = half(wide(title));
    static const std::wregex group(L"[\\(\\[]([^\\(\\)\\[\\]]+)[\\)\\]]"),
        credit(L"^(?:feat(?:uring)?|ft)\\.?\\s+(.+)$", std::regex::icase);
    std::wstring core;
    size_t previous = 0;
    for (std::wsregex_iterator it(text.begin(), text.end(), group), end; it != end; ++it)
    {
        auto &m = *it;
        core += text.substr(previous, m.position() - previous);
        auto annotation = trim(m[1]);
        core += std::regex_match(annotation, credit) ? L" " : m.str();
        previous = m.position() + m.length();
    }
    return normalize(core + text.substr(previous));
}
inline std::set<std::wstring> featuredRelease(const std::string &album)
{
    static const std::wregex credit(L"^(?:feat(?:uring)?|ft)\\.?\\s+(.+)$", std::regex::icase);
    std::set<std::wstring> out;
    for (auto &group : groups(half(wide(album))))
    {
        std::wsmatch m;
        auto a = trim(group);
        if (std::regex_match(a, m, credit))
        {
            auto names = artists(m[1], true);
            out.insert(names.begin(), names.end());
        }
    }
    return out;
}
inline bool sameAlbum(const std::string &a, const std::string &b)
{
    auto normalized = duplicateAlbum(normalize(wide(a)));
    return !normalized.empty() && normalized == duplicateAlbum(normalize(wide(b)));
}
inline bool annotatedArtists(const std::string &text)
{
    static const std::wregex credit(L"\\b(?:with|feat(?:uring)?|ft)\\b|合唱|合作", std::regex::icase);
    for (auto &group : groups(half(wide(text))))
        if (std::regex_search(group, credit))
            return true;
    return false;
}
inline bool corroboratedRelease(const std::string &queryAlbum, const std::set<std::wstring> &queryArtists,
                                const std::string &candidateAlbum, const std::set<std::wstring> &candidateArtists)
{
    if (queryArtists == candidateArtists || sameAlbum(queryAlbum, candidateAlbum) ||
        withoutFeatured(queryAlbum) != withoutFeatured(candidateAlbum))
        return false;
    auto qc = featuredRelease(queryAlbum), cc = featuredRelease(candidateAlbum);
    if (!subset(qc, queryArtists) || !subset(cc, candidateArtists))
        return false;
    auto diff = [](const auto &a, const auto &b) {
        std::set<std::wstring> out;
        std::set_difference(a.begin(), a.end(), b.begin(), b.end(), std::inserter(out, out.end()));
        return out;
    };
    return diff(queryArtists, candidateArtists) == diff(qc, cc) && diff(candidateArtists, queryArtists) == diff(cc, qc);
}
inline std::vector<Candidate> rank(const Snapshot &track, const Json &songs, const Aliases &aliases)
{
    std::vector<Candidate> result;
    int index = 0, seconds = static_cast<int>(std::round(track.duration / 1000));
    for (auto &song : songs)
        result.push_back(score(track, song, aliases, ++index));
    const auto qa = artists(wide(track.artist));
    auto [queryCore, queryFeatured] = trackParts(track.title);
    auto requestedArtists = artists(wide(track.artist), true);
    requestedArtists.insert(queryFeatured.begin(), queryFeatured.end());
    // Complete artist metadata and album/duration evidence break ties only when a fully corroborated result exists.
    for (auto &c : result)
        for (auto &preferred : result)
        {
            if (&c == &preferred || !preferred.high || preferred.penalty || preferred.flags != c.flags || seconds <= 0)
                continue;
            bool strongMeta = c.title >= 98 && c.album >= 95 && c.penalty == 0 && c.seconds > 0 &&
                              std::abs(c.seconds - seconds) <= 20;
            auto ca = artists(wide(c.song.value("artist", ""))), pa = artists(wide(preferred.song.value("artist", "")));
            if (strongMeta && preferred.title >= 98 && preferred.album >= 95 && preferred.score >= 90 && qa == pa &&
                qa != ca && (subset(qa, ca) || subset(ca, qa)) && std::abs(c.seconds - preferred.seconds) <= 2 &&
                std::abs(c.score - preferred.score) < 5)
            {
                c.score = std::min(c.score, preferred.score - 6);
                c.high = false;
            }
            bool knownAlbum = c.title >= 98 && c.artist >= 95 && c.duration == 100 && c.penalty == 0 &&
                              preferred.album == 100 && std::abs(c.seconds - preferred.seconds) <= 2;
            bool conflictingDuration = std::abs(c.seconds - seconds) > 20;
            if (c.song.value("album", "").empty() && exact(preferred) && preferred.duration == 100 &&
                preferred.score >= 95 && c.seconds > 0 && (knownAlbum || conflictingDuration) &&
                c.score > preferred.score - 8)
            {
                c.score = preferred.score - 8;
                c.high = false;
            }
        }
    // Exact album track credit is authoritative; album-level featured/language labels never rewrite the requested
    // artist list.
    if (!track.artist.empty() && !track.album.empty() && seconds > 0 && !queryCore.empty())
    {
        std::vector<Candidate> exactTracks;
        for (auto &c : result)
            if (normalize(wide(c.song.value("title", ""))) == normalize(wide(track.title)) &&
                requestedArtists == artists(wide(c.song.value("artist", "")), true) &&
                sameAlbum(track.album, c.song.value("album", "")) && c.seconds > 0 &&
                std::abs(c.seconds - seconds) <= 2 && c.penalty == 0 && c.high && c.score >= 95)
                exactTracks.push_back(c);
        for (auto &c : result)
        {
            if (exactTracks.empty() || c.seconds <= 0 || std::abs(c.seconds - seconds) > 2)
                continue;
            if (std::any_of(exactTracks.begin(), exactTracks.end(),
                            [&](const auto &v) { return v.song.value("mid", "") == c.song.value("mid", ""); }))
                continue;
            auto [core, featured] = trackParts(c.song.value("title", ""));
            auto ca = artists(wide(c.song.value("artist", "")), true);
            if (core != queryCore || ca.empty() || !(subset(ca, requestedArtists) || subset(requestedArtists, ca)))
                continue;
            bool languageDifference = sameAlbum(track.album, c.song.value("album", "")) &&
                                      ((versions(track.title) ^ versions(c.song.value("title", ""))) & Languages);
            bool missingFeature =
                std::any_of(queryFeatured.begin(), queryFeatured.end(),
                            [&](const auto &n) { return !featured.contains(n) && !ca.contains(n); }) &&
                subset(ca, requestedArtists) &&
                (sameAlbum(track.album, c.song.value("album", "")) ||
                 withoutFeatured(track.album) == withoutFeatured(c.song.value("album", "")));
            bool creditDifference =
                !annotatedArtists(track.artist) && !annotatedArtists(c.song.value("artist", "")) &&
                subset(featured, ca) &&
                std::all_of(exactTracks.begin(), exactTracks.end(),
                            [](const auto &v) { return !annotatedArtists(v.song.value("artist", "")); }) &&
                std::any_of(exactTracks.begin(), exactTracks.end(),
                            [&](const auto &v) { return v.flags == c.flags; }) &&
                corroboratedRelease(track.album, requestedArtists, c.song.value("album", ""), ca);
            if (!languageDifference && !missingFeature && !creditDifference)
                continue;
            for (auto &preferred : exactTracks)
                if (std::abs(c.seconds - preferred.seconds) <= 2 && c.score > preferred.score - 6)
                {
                    c.score = preferred.score - 6;
                    c.high = false;
                }
        }
    }
    std::stable_sort(result.begin(), result.end(), [](const Candidate &a, const Candidate &b) {
        if (a.score != b.score)
            return a.score > b.score;
        if (a.artist != b.artist)
            return a.artist > b.artist;
        return a.rank < b.rank;
    });
    if (result.size() > 1 && result[0].high && result[0].score - result[1].score < 5 &&
        !equivalent(result[0], result[1]))
        result[0].high = false;
    return result;
}
inline bool canBind(const std::vector<Candidate> &ranked, const Snapshot &track)
{
    if (ranked.empty() || !ranked[0].high || (ranked[0].score < 90 && !strong(ranked[0])))
        return false;
    uint32_t input = versions(track.title, track.album), flags = ranked[0].flags;
    if (((flags ^ input) & Languages) ||
        ((flags & ~input) & (Japanese | Chinese | English | Cover | Remix | Instrumental | SpedUp | Slowed | Live)))
        return false;
    return ranked.size() < 2 || ranked[0].score - ranked[1].score >= 5 || equivalent(ranked[0], ranked[1]);
}
struct QueryPlan
{
    int phase;
    std::string keyword;
};
inline std::vector<QueryPlan> queryPlans(const Snapshot &track, const Aliases &aliases)
{
    std::vector<QueryPlan> result;
    std::set<std::wstring> seen;
    auto add = [&](int phase, const std::string &text) {
        auto value = collapse(wide(text));
        if (!value.empty() && seen.insert(half(value)).second)
            result.push_back({phase, utf8(value)});
    };
    add(1, track.title + " " + track.artist);
    add(2, track.title);
    auto simple = utf8(simplified(wide(track.title)));
    if (simple != track.title)
    {
        add(2, simple + " " + track.artist);
        add(2, simple);
    }
    for (auto &alias : aliases.names(track.artist))
    {
        add(3, track.title + " " + alias);
        if (simple != track.title)
            add(3, simple + " " + alias);
    }
    if (!track.album.empty())
        add(2, track.title + " " + track.album);
    return result;
}
inline std::string describe(uint32_t flags)
{
    const std::pair<uint32_t, const char *> names[] = {
        {Live, "现场"},   {Japanese, "日语版"}, {Chinese, "中文版"},      {Korean, "韩语版"},      {English, "英语版"},
        {Remix, "混音"},  {Cover, "翻唱"},      {Instrumental, "纯音乐"}, {Acoustic, "原声"},      {Remaster, "重制"},
        {SpedUp, "加速"}, {Slowed, "慢速"},     {Demo, "小样"},           {ReRecorded, "重新录制"}};
    std::string result;
    for (auto [flag, name] : names)
        if (flags & flag)
        {
            if (!result.empty())
                result += " · ";
            result += name;
        }
    return result;
}
inline std::vector<std::string> queries(const Snapshot &track, const Aliases &aliases)
{
    std::vector<std::string> result;
    for (auto &plan : queryPlans(track, aliases))
        result.push_back(plan.keyword);
    return result;
}
} // namespace lyrics::matching

inline lyrics::matching::Aliases::Aliases()
{
    add({"脸红的思春期", "BOL4", "Bolbbalgan4", "볼빨간사춘기"});
    add({"周杰伦", "Jay Chou", "周杰倫"});
    add({"林俊杰", "JJ Lin", "林俊傑"});
    add({"邓紫棋", "G.E.M.", "G.E.M.邓紫棋", "鄧紫棋"});
    add({"陈奕迅", "Eason Chan", "陳奕迅"});
    add({"薛之谦", "Joker Xue"});
    add({"BTS", "防弹少年团", "방탄소년단", "Bangtan"});
    add({"BLACKPINK", "블랙핑크"});
    add({"IU", "아이유", "李知恩"});
    add({"YOASOBI", "ヨアソビ"});
    add({"米津玄师", "Kenshi Yonezu", "米津玄師"});
    add({"NewJeans", "뉴진스"});
    add({"aespa", "에스파"});
    add({"SEVENTEEN", "세븐틴", "SVT"});
    add({"TWICE", "트와이스"});
    add({"Taylor Swift", "泰勒·斯威夫特", "泰勒斯威夫特"});
    add({"The Weeknd", "威肯"});
    add({"Ariana Grande", "爱莉安娜·格兰德"});
}
