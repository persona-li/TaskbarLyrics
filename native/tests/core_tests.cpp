#include "core.hpp"
#include <iostream>
using namespace lyrics;
void check(bool value, const char *name)
{
    if (!value)
        throw std::runtime_error(name);
}
int main()
{
    try
    {
        auto p = palette(264.540965113);
        check(p.first == "#FF496DBF" && p.second == "#FFA0CCEE", "exact default palette");
        for (int h = -360; h <= 720; ++h)
            check(palette(h) == palette(h + 360), "hue normalization");
        auto l = parseLrc("[offset:100]\n[00:02.30][00:04.005]重复\n[00:01.2]开头\n[ar:artist]");
        check(l.size() == 3 && l[0].start == 1300 && l[2].start == 4105, "LRC fraction/offset/multiple stamps/order");
        check(currentLine(l, 1299) == -1 && currentLine(l, 1300) == 0 && currentLine(l, 2400) == 1, "line boundaries");
        Line q{"ab", "", 100, 100, {{"a", 100, 50}, {"b", 150, 50}}};
        check(progress(q, 125) == .25 && progress(q, 200) == 1, "QRC timing");
        check(progress(l[0], 1300) == 0 && progress(l[0],1850)==.5, "LRC continuous line progress parity");
        std::vector<Line> hold={{"a","",0,1000,{}},{"b","",5000,1000,{}}};
        check(resolvedLine(hold,2500)==0 && resolvedLine(hold,2501)==-1 && resolvedLine(hold,5000)==1,"long interlude clear and next line");
        hold[1].start=3000;check(resolvedLine(hold,2999)==0,"short interlude hold");
        check(accepts(2, "new", 2, "new") && !accepts(2, "new", 1, "old") && !accepts(2, "new", 2, "old"),
              "late lyric/error results must not replace new track");
        auto a = choose({8, 500}, {800, 1400}, 200, 400);
        check(a.left == 8 && a.width == 400 && !a.right, "prefer left");
        a = choose({8, 50}, {800, 1400}, 200, 400);
        check(a.left == 1000 && a.right, "right align fallback");
        check(choose({0, 50}, {800, 850}, 200, 400).width == 0, "no space hides");
        auto g = gap(0, 500, {{200, 300}, {100, 220}}, 8, 20);
        check(g.left == 308 && g.right == 480, "merged occupied gap");
        std::cout << "native invariants passed\n";
        return 0;
    }
    catch (const std::exception &e)
    {
        std::cerr << e.what() << '\n';
        return 1;
    }
}
