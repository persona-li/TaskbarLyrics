#pragma once
#include "core.hpp"
namespace lyrics
{
struct TaskbarGeometry
{
    int left{}, top{}, right{}, bottom{}, start{};
    Interval leftGap{}, rightGap{};
    bool measured{}, available{};
    bool operator==(const TaskbarGeometry &o) const
    {
        return left == o.left && top == o.top && right == o.right && bottom == o.bottom && start == o.start &&
               measured == o.measured && available == o.available && leftGap.left == o.leftGap.left &&
               leftGap.right == o.leftGap.right && rightGap.left == o.rightGap.left &&
               rightGap.right == o.rightGap.right;
    }
};
class GeometryStability
{
    TaskbarGeometry candidate{};
    bool has{};
    int samples{};
    double since{};

  public:
    bool observe(const TaskbarGeometry &next, double now)
    {
        if (!has || !(candidate == next))
        {
            candidate = next;
            has = true;
            samples = 1;
            since = now;
        }
        else
            ++samples;
        return samples >= 3 && now - since >= (next.measured ? 140 : 400);
    }
};
inline Placement stableChoose(Interval left, Interval right, int minimum, int maximum, bool previousRight,
                              int hysteresis)
{
    int threshold = minimum + (previousRight ? std::max(0, hysteresis) : 0);
    if (left.right - left.left >= threshold)
        return choose(left, {}, minimum, maximum);
    if (right.right - right.left >= minimum)
        return choose({}, right, minimum, maximum);
    return choose(left, right, minimum, maximum);
}
inline std::string scriptFamily(char32_t c)
{
    if ((c >= 0x3040 && c <= 0x30ff) || (c >= 0xff66 && c <= 0xff9f))
        return "japanese";
    if ((c >= 0xac00 && c <= 0xd7af) || (c >= 0x1100 && c <= 0x11ff) || (c >= 0x3130 && c <= 0x318f))
        return "korean";
    if ((c >= 0x3400 && c <= 0x9fff) || (c >= 0x20000 && c <= 0x323af))
        return "chinese";
    if (c >= 0x0400 && c <= 0x052f)
        return "cyrillic";
    if ((c >= 0x0600 && c <= 0x06ff) || (c >= 0x0750 && c <= 0x077f) || (c >= 0xfb50 && c <= 0xfdff))
        return "arabic";
    if (c <= 0x024f)
        return "latin";
    return "other";
}
inline int manualTaskbarWidth(int fullWidth,int minimum,int maximum,int leftInset,int rightInset,double ratio)
{
    if(fullWidth<=0)return std::max(1,minimum);
    int requested=std::clamp(static_cast<int>(std::lround(fullWidth*std::clamp(ratio,.05,.69))),minimum,std::max(minimum,maximum));
    requested=std::min(requested,static_cast<int>(std::floor(fullWidth*.69)));
    return std::max(1,std::min(requested,std::max(1,fullWidth-leftInset-rightInset)));
}
} // namespace lyrics
