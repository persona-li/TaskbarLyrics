#pragma once
#include "platform.hpp"
namespace lyrics
{
// Offline harness override, scoped to the test thread. Real application callers never install one.
class ScopedLibraryTransport
{
    std::function<std::string(const std::wstring &)> previous;

  public:
    explicit ScopedLibraryTransport(std::function<std::string(const std::wstring &)> request);
    ~ScopedLibraryTransport();
    ScopedLibraryTransport(const ScopedLibraryTransport &) = delete;
    ScopedLibraryTransport &operator=(const ScopedLibraryTransport &) = delete;
};
inline fs::path importedLyricsPath(const fs::path &root, const std::string &key)
{
    uint64_t hash = 14695981039346656037ull;
    for (unsigned char c : key)
    {
        hash ^= c;
        hash *= 1099511628211ull;
    }
    std::ostringstream s;
    s << std::hex << hash;
    return root / L"Imported" / (s.str() + ".lrc");
}
} // namespace lyrics
