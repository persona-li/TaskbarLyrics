#pragma once
#include <algorithm>
#include <cmath>
#include <string>
namespace lyrics
{
// All time inputs are milliseconds supplied by the adapter; tests never use a
// wall clock.
class TimelineClock
{
    bool initialized{}, playing{}, accepted{};
    double anchor{}, at{}, rate{1}, raw{}, stamp{}, resumeAt{}, scrubUntil{};
    double candidate{}, candidateStamp{}, candidateAt{}, candidateStarted{};
    int candidateCount{};

  public:
    void reset()
    {
        *this = {};
    }
    double position(double now) const
    {
        return std::max(0., anchor + (playing ? std::max(0., now - at) * rate : 0.));
    }
    void seek(double value, double now)
    {
        anchor = std::max(0., value);
        at = now;
        initialized = true;
        candidateCount = 0;
        accepted = false;
        scrubUntil = now + 600;
    }
    double sample(double value, double updated, double wallNow, double now, bool running, double speed = 1)
    {
        if (!(speed > 0) || !std::isfinite(speed))
            speed = 1;
        const bool changed = running != playing;
        if (!initialized)
        {
            anchor = std::max(0., value);
            at = now;
            playing = running;
            rate = speed;
            initialized = true;
        }
        if (changed)
        {
            anchor = position(now);
            at = now;
            playing = running;
            rate = speed;
            candidateCount = 0;
            accepted = false;
            if (running)
                resumeAt = wallNow;
        }
        if (accepted && !changed && value == raw && updated == stamp && speed == rate)
            return position(now);
        if (accepted && updated < stamp - 30)
            return position(now);
        if (!running)
        {
            if (std::abs(value - anchor) < 300)
                candidateCount = 0;
            else if (!candidateCount || std::abs(value - candidate) > 300)
            {
                candidate = value;
                candidateStamp = updated;
                candidateAt = now;
                candidateStarted = now;
                candidateCount = 1;
            }
            else if (updated > candidateStamp || (value != candidate && now - candidateAt >= 80))
            {
                candidate = value;
                candidateStamp = updated;
                candidateAt = now;
                ++candidateCount;
                if (candidateCount >= 2 && now - candidateStarted >= 80)
                {
                    anchor = value;
                    at = now;
                    candidateCount = 0;
                }
            }
            raw = value;
            stamp = updated;
            accepted = true;
            return position(now);
        }
        if (resumeAt && updated < resumeAt - 100)
            return position(now);
        resumeAt = 0;
        const bool advanced = accepted && updated > stamp;
        const double expected = raw + (advanced ? (updated - stamp) * rate : 0.);
        const bool discontinuity = accepted && std::abs(value - expected) >= 250;
        if (discontinuity)
            scrubUntil = now + 600;
        const double age = wallNow - updated;
        const double adjusted = value + (advanced && age >= 0 && age <= 1000 ? age * speed : 0.);
        const double estimate = position(now), error = adjusted - estimate;
        if (!accepted || discontinuity || now < scrubUntil || std::abs(error) > 600)
            anchor = adjusted;
        else if (std::abs(error) > 180)
            anchor = estimate + error * .4;
        else if (std::abs(error) > 80)
            anchor = estimate + error * .2;
        else
            anchor = estimate;
        at = now;
        rate = speed;
        raw = value;
        stamp = updated;
        accepted = true;
        return position(now);
    }
};
class NavigationTransport
{
    double deadline{}, stableSince{};
    bool held{};
    std::string origin;

  public:
    void clear()
    {
        origin.clear();
        stableSince = 0;
    }
    void begin(const std::string &key, bool playing, double now)
    {
        origin = key;
        held = playing;
        deadline = now + 900;
        stableSince = 0;
    }
    bool read(const std::string &key, bool playing, double now)
    {
        if (origin.empty() || key.empty() || now >= deadline)
        {
            origin.clear();
            return playing;
        }
        if (playing)
        {
            held = true;
            if (key != origin && !stableSince)
                stableSince = now;
        }
        else
            stableSince = 0;
        if (stableSince && now - stableSince >= 200)
        {
            origin.clear();
            return playing;
        }
        return playing || held;
    }
};
} // namespace lyrics
