#include "platform.hpp"
#include "playback_clock.hpp"
#include <condition_variable>
#include <deque>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Media.Control.h>
namespace lyrics
{
using namespace winrt::Windows::Media::Control;
struct Media::Impl
{
    std::function<void(Snapshot)> callback;
    std::mutex mutex;
    std::condition_variable wake;
    std::deque<Json> commands;
    std::atomic<bool> dirty{true}, propertiesDirty{true}, sessionsDirty{true};
    std::jthread worker;
    explicit Impl(std::function<void(Snapshot)> cb)
        : callback(std::move(cb)), worker([this](std::stop_token stop) { run(stop); })
    {
    }
    ~Impl()
    {
        worker.request_stop();
        wake.notify_all();
        if (worker.joinable())
            worker.join();
    }
    void signal(bool metadata = false, bool sessions = false)
    {
        if (metadata)
            propertiesDirty = true;
        if (sessions)
            sessionsDirty = true;
        dirty = true;
        wake.notify_all();
    }
    void run(std::stop_token stop)
    {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        GlobalSystemMediaTransportControlsSessionManager manager{nullptr};
        GlobalSystemMediaTransportControlsSession session{nullptr};
        winrt::event_token managerToken{}, mediaToken{}, playbackToken{}, timelineToken{};
        bool managerHooked = false, sessionHooked = false;
        Snapshot last;
        TimelineClock clock;
        NavigationTransport presentation;
        double nextEnumeration = 0, nextProperties = 0;
        auto unhook = [&] {
            if (session && sessionHooked)
                try
                {
                    session.MediaPropertiesChanged(mediaToken);
                    session.PlaybackInfoChanged(playbackToken);
                    session.TimelinePropertiesChanged(timelineToken);
                }
                catch (...)
                {
                }
            sessionHooked = false;
            session = nullptr;
        };
        while (!stop.stop_requested())
        {
            Snapshot s = last;
            s.error.clear();
            s.captured = ticks();
            dirty = false;
            try
            {
                if (!manager)
                {
                    manager = GlobalSystemMediaTransportControlsSessionManager::RequestAsync().get();
                    managerToken = manager.SessionsChanged([this](auto const &, auto const &) { signal(true, true); });
                    managerHooked = true;
                    sessionsDirty = true;
                }
                if (sessionsDirty.exchange(false) || ticks() >= nextEnumeration)
                {
                    auto all = manager.GetSessions();
                    GlobalSystemMediaTransportControlsSession selected{nullptr};
                    int best = -1;
                    bool currentExists = false;
                    for (auto item : all)
                    {
                        if (session && winrt::get_abi(session) == winrt::get_abi(item))
                            currentExists = true;
                        auto id = winrt::to_string(item.SourceAppUserModelId());
                        std::transform(id.begin(), id.end(), id.begin(),
                                       [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
                        auto status = item.GetPlaybackInfo().PlaybackStatus();
                        int rank = 0;
                        for (auto k : {"qq", "qqmusic", "tencent"})
                            if (id.find(k) != std::string::npos)
                                rank += 10;
                        rank += status == GlobalSystemMediaTransportControlsSessionPlaybackStatus::Playing  ? 3
                                : status == GlobalSystemMediaTransportControlsSessionPlaybackStatus::Paused ? 1
                                                                                                            : 0;
                        if (rank > best)
                        {
                            best = rank;
                            selected = item;
                        }
                    }
                    // Preserve the actual COM session, never rely on an AppId surviving a
                    // player restart.
                    if (!currentExists || !session || (selected && selected != session && best >= 10))
                    {
                        unhook();
                        session = selected;
                        clock.reset();
                        presentation.clear();
                        last = {};
                        s = {};
                        propertiesDirty = true;
                        if (session)
                        {
                            mediaToken =
                                session.MediaPropertiesChanged([this](auto const &, auto const &) { signal(true); });
                            playbackToken =
                                session.PlaybackInfoChanged([this](auto const &, auto const &) { signal(); });
                            timelineToken =
                                session.TimelinePropertiesChanged([this](auto const &, auto const &) { signal(); });
                            sessionHooked = true;
                        }
                    }
                    nextEnumeration = ticks() + 1000;
                }
                std::deque<Json> pending;
                {
                    std::lock_guard guard(mutex);
                    pending.swap(commands);
                }
                if (session)
                {
                    for (auto &c : pending)
                    {
                        auto op = c.value("type", "");
                        bool ok = true;
                        if (op == "toggle" || op == "pause" || op == "play")
                            presentation.clear();
                        if (op == "next" || op == "previous")
                            presentation.begin(last.key(), last.playing, ticks());
                        if (op == "toggle")
                        {
                            auto info=session.GetPlaybackInfo();
                            if(info.Controls().IsPlayPauseToggleEnabled()) ok=session.TryTogglePlayPauseAsync().get();
                            else if(info.PlaybackStatus()==GlobalSystemMediaTransportControlsSessionPlaybackStatus::Playing)ok=session.TryPauseAsync().get();
                            else ok=session.TryPlayAsync().get();
                        }
                        else if (op == "play")
                            ok = session.TryPlayAsync().get();
                        else if (op == "pause")
                            ok = session.TryPauseAsync().get();
                        else if (op == "previous")
                            ok = session.TrySkipPreviousAsync().get();
                        else if (op == "next")
                            ok = session.TrySkipNextAsync().get();
                        else if (op == "seek")
                        {
                            double target = std::clamp(c.value("position", 0.), 0., std::max(last.duration, 0.));
                            ok = session.TryChangePlaybackPositionAsync(static_cast<int64_t>(target * 10000)).get();
                            if (ok)
                                clock.seek(target, ticks());
                        }
                        if (!ok)
                            s.error = "播放器未执行此操作";
                        signal(op == "previous" || op == "next");
                    }
                    if (propertiesDirty.exchange(false) || ticks() >= nextProperties)
                    {
                        auto props = session.TryGetMediaPropertiesAsync().get();
                        std::string title = winrt::to_string(props.Title()), artist = winrt::to_string(props.Artist()),
                                    album = winrt::to_string(props.AlbumTitle());
                        if (title != s.title || artist != s.artist || album != s.album)
                            clock.reset();
                        s.title = std::move(title);
                        s.artist = std::move(artist);
                        s.album = std::move(album);
                        nextProperties = ticks() + 1500;
                    }
                    auto info = session.GetPlaybackInfo();
                    auto time = session.GetTimelineProperties();
                    auto status = info.PlaybackStatus();
                    s.playing = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus::Playing;
                    switch (status)
                    {
                    case GlobalSystemMediaTransportControlsSessionPlaybackStatus::Playing:
                        s.status = "Playing";
                        break;
                    case GlobalSystemMediaTransportControlsSessionPlaybackStatus::Paused:
                        s.status = "Paused";
                        break;
                    case GlobalSystemMediaTransportControlsSessionPlaybackStatus::Changing:
                        s.status = "Changing";
                        break;
                    case GlobalSystemMediaTransportControlsSessionPlaybackStatus::Stopped:
                        s.status = "Stopped";
                        break;
                    case GlobalSystemMediaTransportControlsSessionPlaybackStatus::Opened:
                        s.status = "Opened";
                        break;
                    default:
                        s.status = "Closed";
                        break;
                    }
                    s.source = winrt::to_string(session.SourceAppUserModelId());
                    s.duration = std::max(
                        0., std::chrono::duration<double, std::milli>(time.EndTime() - time.StartTime()).count());
                    if(s.duration<=0) s.duration=std::max(0.,std::chrono::duration<double,std::milli>(time.EndTime()).count());
                    auto controls = info.Controls();
                    s.previous = controls.IsPreviousEnabled();
                    s.next = controls.IsNextEnabled();
                    s.toggle = controls.IsPlayPauseToggleEnabled() || (s.playing?controls.IsPauseEnabled():controls.IsPlayEnabled());
                    s.seek = controls.IsPlaybackPositionEnabled();
                    s.captured = ticks();
                    auto updated =
                        std::chrono::duration<double, std::milli>(time.LastUpdatedTime().time_since_epoch()).count();
                    auto wallNow =
                        std::chrono::duration<double, std::milli>(winrt::clock::now().time_since_epoch()).count();
                    auto raw = std::chrono::duration<double, std::milli>(time.Position()).count();
                    auto speed = info.PlaybackRate();
                    double playbackRate=speed?speed.Value():1.;
                    if(!(playbackRate>0)||!std::isfinite(playbackRate))playbackRate=1;
                    s.playbackRate=playbackRate;
                    s.position = clock.sample(raw, updated, wallNow, s.captured, s.playing, playbackRate);
                    s.position = std::clamp(s.position, 0., s.duration>0?s.duration:std::max(0.,s.position));
                    if(s.title.empty()||s.status=="Closed"||s.status=="Opened")presentation.clear();
                    s.presentationPlaying = presentation.read(s.key(), s.playing, s.captured);
                }
                else
                {
                    s = {};
                    s.status = "Closed";
                    s.captured = ticks();
                }
                last = s;
            }
            catch (const winrt::hresult_error &e)
            {
                s.error = "媒体会话暂不可用：" + winrt::to_string(e.message());
                unhook();
                if (manager && managerHooked)
                    try
                    {
                        manager.SessionsChanged(managerToken);
                    }
                    catch (...)
                    {
                    }
                managerHooked = false;
                manager = nullptr;
                propertiesDirty = true;
            }
            catch (const std::exception &e)
            {
                s.error = e.what();
            }
            callback(std::move(s));
            std::unique_lock lock(mutex);
            wake.wait_for(lock, std::chrono::milliseconds(last.playing ? 75 : 200),
                          [&] { return stop.stop_requested() || dirty.load() || !commands.empty(); });
        }
        unhook();
        if (manager && managerHooked)
            try
            {
                manager.SessionsChanged(managerToken);
            }
            catch (...)
            {
            }
        winrt::uninit_apartment();
    }
};
Media::Media(std::function<void(Snapshot)> cb) : impl(std::make_unique<Impl>(std::move(cb)))
{
}
Media::~Media() = default;
void Media::command(Json request)
{
    {
        std::lock_guard guard(impl->mutex);
        impl->commands.push_back(std::move(request));
    }
    impl->wake.notify_all();
}
} // namespace lyrics
