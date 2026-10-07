#include "playback_clock.hpp"
#include "taskbar_policy.hpp"
#include <iostream>
#include <stdexcept>
using namespace lyrics;
void require(bool b, const char *message)
{
    if (!b)
        throw std::runtime_error(message);
}
int main()
{
    try
    {
        TimelineClock clock;
        require(clock.sample(1000, 10000, 10000, 0, true) == 1000, "initial timeline");
        require(clock.sample(1000, 10000, 10500, 500, true) == 1500,
                "stale repeated sample must not reset running clock");
        require(clock.sample(1300, 10300, 10300, 600, false) == 1600, "pause freezes estimate instead of rollback raw");
        require(clock.sample(0, 10400, 10400, 700, false) == 1600, "paused anomaly must not seek");
        require(clock.sample(0, 10400, 10500, 800, false) == 1600, "poll replay is not fresh confirmation");
        require(clock.sample(1550, 10500, 10500, 900, false) == 1600, "near freeze cancels seek candidate");
        require(clock.sample(5000, 10600, 10600, 1000, false) == 1600, "paused seek first evidence");
        require(clock.sample(5000, 10700, 10700, 1100, false) == 5000, "two fresh paused samples confirm seek");
        require(clock.sample(1300, 10300, 10800, 1200, true) == 5000, "stale timeline after resume ignored");
        clock.seek(3000, 1300);
        require(clock.position(1300) == 3000, "explicit seek reanchors immediately");
        NavigationTransport label;
        label.begin("old", true, 1000);
        require(label.read("old", false, 1050), "skip transient pause held");
        require(label.read("new", true, 1100), "skip new playing");
        require(label.read("new", false, 1150), "skip brief rollback held");
        require(!label.read("new", false, 1901), "real pause remains visible after grace");
        GeometryStability stable;
        TaskbarGeometry g{0, 1000, 1920, 1048, 800, {8, 780}, {1200, 1700}, true, true};
        require(!stable.observe(g, 0) && !stable.observe(g, 80) && stable.observe(g, 160),
                "full geometry settles after three observations");
        auto changed = g;
        changed.rightGap.right = 1699;
        require(!stable.observe(changed, 170), "last pixel update restarts geometry confirmation");
        auto p = stableChoose({8, 250}, {900, 1300}, 200, 400, true, 80);
        require(p.right && p.left == 900, "side hysteresis avoids repeated left/right movement");
        require(stableChoose({8, 10}, {900, 910}, 200, 400, true, 80).width == 0, "measured no space must hide");
        require(manualTaskbarWidth(1920,120,768,8,20,.6)==768,"measurement-failure manual width respects adaptive max");
        require(manualTaskbarWidth(1000,750,1350,8,40,.6)==690,"manual width respects screen hard cap");
        require(scriptFamily(U'あ') == "japanese" && scriptFamily(U'한') == "korean" &&
                    scriptFamily(U'字') == "chinese" && scriptFamily(U'A') == "latin",
                "configured language families");
        std::cout << "platform policy scenarios passed\n";
        return 0;
    }
    catch (const std::exception &e)
    {
        std::cerr << e.what() << '\n';
        return 1;
    }
}
