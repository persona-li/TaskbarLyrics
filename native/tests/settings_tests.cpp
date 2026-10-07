#include "settings.hpp"
#include <iostream>
#include <stdexcept>
using namespace lyrics;
void require(bool value, const char *message)
{
    if (!value)
        throw std::runtime_error(message);
}
int main()
{
    try
    {
        require(lowerInvariant(L"ЗЕМФИРА ÉCOLE") == L"земфира école",
                "Unicode invariant case retained without process locale");
        Json old = {{"display",
                     {{"fontSize", 35.},
                      {"fonts", {{"chinese", "User font"}, {"japanese", "Japanese font"}}},
                      {"normalColor", "#FF123456"},
                      {"shadowColor", "#80010203"},
                      {"textAlignment", "Right"}}},
                    {"general",
                     {{"theme", "Light"},
                      {"showOverlay", false},
                      {"reduceMotion", true},
                      {"recentNormalColors", {"#FF123456"}}}},
                    {"overlay", {{"autoFit", false}, {"widthRatio", .31}}},
                    {"lyrics", {{"globalOffsetMs", 150}}}};
        auto s = migrateSettings(old);
        normalizeSettings(s);
        require(s["font"] == "User font" && s["fonts"]["japanese"] == "Japanese font", "configured fonts retained");
        require(s["fontSize"] == 35 && s["normal"] == "#FF123456" && s["shadowColor"] == "#80010203",
                "appearance values retained");
        require(s["theme"] == "light" && s["overlay"] == false && s["animations"] == false,
                "general controls retained");
        require(s["alignment"] == "Right" && s["autoLayout"] == false && s["fallbackWidthPercent"] == 31.,
                "placement retained");
        require(s["globalOffset"] == 150 && s["recentColors"]["normal"].size() == 1,
                "timing and role-specific history retained");
        require(old["display"]["fonts"]["chinese"] == "User font", "migration input remains unchanged");
        s["hue"] = -1;
        s["recentColors"]["normal"] = {"#FF123456", "#FF123456", "oops", "#FFABCDEF"};
        s["recentColors"]["highlight"] = {"#FFA0CCEE"};
        normalizeSettings(s);
        require(s["hue"] == -1 && s["recentColors"]["normal"].size() == 2 && s["recentColors"]["highlight"].size() == 1,
                "manual state and independent histories normalized");
        s["fontSize"] = 100.;
        s["globalOffset"] = 99999;
        normalizeSettings(s);
        require(s["fontSize"] == 72. && s["globalOffset"] == 5000, "original setting ranges preserved");
        std::cout << "Settings migration invariants passed\n";
        return 0;
    }
    catch (const std::exception &e)
    {
        std::cerr << e.what() << '\n';
        return 1;
    }
}
