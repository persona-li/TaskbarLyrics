#pragma once
#include "platform.hpp"
#include <chrono>
namespace lyrics
{
class Logger
{
    fs::path directory, path;
    std::ofstream output;
    uintmax_t written{}, allowance{};
    std::string day;
    static constexpr uintmax_t segment = 2 * 1024 * 1024, maximum = 20 * 1024 * 1024;
    void open(const std::string &date)
    {
        output.close();
        day = date;
        written = 0;
        fs::create_directories(directory);
        std::vector<fs::directory_entry> files;
        uintmax_t total = 0;
        auto now = fs::file_time_type::clock::now();
        for (auto &f : fs::directory_iterator(directory))
            if (f.is_regular_file() && f.path().filename().wstring().starts_with(L"native-") &&
                f.path().extension() == L".log")
            {
                if (now - f.last_write_time() > std::chrono::hours(24 * 7))
                {
                    std::error_code e;
                    fs::remove(f.path(), e);
                    if (!e)
                        continue;
                }
                files.push_back(f);
                total += f.file_size();
            }
        std::sort(files.begin(), files.end(),
                  [](auto &a, auto &b) { return a.last_write_time() < b.last_write_time(); });
        for (auto &f : files)
        {
            if (total <= maximum - segment)
                break;
            std::error_code e;
            auto n = f.file_size();
            fs::remove(f.path(), e);
            if (!e)
                total -= n;
        }
        allowance = total < maximum ? std::min(segment, maximum - total) : 0;
        path = directory / wide("native-" + date + "-" + std::to_string(GetCurrentProcessId()) + "-" +
                                std::to_string(GetTickCount64()) + ".log");
        output.open(path, std::ios::binary | std::ios::app);
    }

  public:
    explicit Logger(fs::path dir) : directory(std::move(dir))
    {
    }
    void write(const std::string &event) noexcept
    {
        try
        {
            SYSTEMTIME time{};
            GetLocalTime(&time);
            char date[16]{};
            sprintf_s(date, "%04d%02d%02d", time.wYear, time.wMonth, time.wDay);
            auto message = event.substr(0, 16384);
            if (!output.is_open() || day != date || written + message.size() + 64 > segment)
                open(date);
            char clock[32]{};
            sprintf_s(clock, "%02d:%02d:%02d.%03d ", time.wHour, time.wMinute, time.wSecond, time.wMilliseconds);
            auto line = std::string(clock) + message + "\n";
            if (written + line.size() > allowance)
                return;
            output << line;
            output.flush();
            written += line.size();
        }
        catch (...)
        {
        }
    }
};
} // namespace lyrics
