#include "platform.hpp"
#include "taskbar_policy.hpp"
#include <UIAutomation.h>
#include <condition_variable>
#include <d2d1.h>
#include <dwrite.h>
#include <shellapi.h>
namespace lyrics
{
namespace
{
std::wstring propertyText(IUIAutomationElement *e, PROPERTYID id)
{
    VARIANT v;
    VariantInit(&v);
    std::wstring result;
    if (SUCCEEDED(e->GetCachedPropertyValue(id, &v)) && v.vt == VT_BSTR && v.bstrVal)
        result = v.bstrVal;
    VariantClear(&v);
    return result;
}
D2D1_COLOR_F brushColor(const std::string &argb)
{
    try
    {
        unsigned long v = std::stoul(argb.substr(1), nullptr, 16);
        return D2D1::ColorF(((v >> 16) & 255) / 255.f, ((v >> 8) & 255) / 255.f, (v & 255) / 255.f,
                            ((v >> 24) & 255) / 255.f);
    }
    catch (...)
    {
        return D2D1::ColorF(.3f, .4f, .7f);
    }
}
LRESULT CALLBACK proc(HWND h, UINT m, WPARAM w, LPARAM l)
{
    if (m == WM_NCHITTEST)
        return HTTRANSPARENT;
    if (m == WM_MOUSEACTIVATE)
        return MA_NOACTIVATE;
    return DefWindowProcW(h, m, w, l);
}
bool equalRect(const RECT &a, const RECT &b)
{
    return a.left == b.left && a.top == b.top && a.right == b.right && a.bottom == b.bottom;
}
class TaskbarEvents : public Microsoft::WRL::RuntimeClass<Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
                                                          IUIAutomationPropertyChangedEventHandler,
                                                          IUIAutomationStructureChangedEventHandler>
{
  public:
    std::function<void()> changed;
    HRESULT STDMETHODCALLTYPE HandlePropertyChangedEvent(IUIAutomationElement *, PROPERTYID, VARIANT) override
    {
        if (changed)
            changed();
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE HandleStructureChangedEvent(IUIAutomationElement *, StructureChangeType,
                                                          SAFEARRAY *) override
    {
        if (changed)
            changed();
        return S_OK;
    }
};
} // namespace
struct Overlay::Impl
{
    HWND window{};
    ComPtr<ID2D1Factory> d2d;
    ComPtr<IDWriteFactory> write;
    ComPtr<ID2D1DCRenderTarget> target;
    ComPtr<IDWriteTextLayout> cachedLayout;
    std::string cachedLayoutKey;
    mutable std::mutex lock;
    Json config = Json::object(), state = Json::object();
    TaskbarGeometry geometry{};
    GeometryStability stability;
    RECT measured{}, safe{}, displayed{};
    bool right{}, valid{}, pending{}, wasVisible{}, displayedRight{};
    double dpi{1}, entryStarted{}, exitStarted{};
    double lineStarted{};
    int phase{}, lastLine{-2}; // 0 stable, 1 exiting, 2 waiting, 3 entering
    float pan{};
    std::wstring previousText;
    std::jthread observer;
    std::mutex observerLock;
    std::condition_variable observerWake;
    std::atomic<bool> observerDirty{true};
    Impl()
    {
        WNDCLASSW wc{};
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.lpfnWndProc = proc;
        wc.lpszClassName = L"TaskbarLyricsNativeOverlay";
        RegisterClassW(&wc);
        window =
            CreateWindowExW(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW, wc.lpszClassName,
                            L"Native lyrics", WS_POPUP, 0, 0, 1, 1, nullptr, nullptr, wc.hInstance, nullptr);
        D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, d2d.GetAddressOf());
        DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory),
                            reinterpret_cast<IUnknown **>(write.GetAddressOf()));
        auto properties =
            D2D1::RenderTargetProperties(D2D1_RENDER_TARGET_TYPE_DEFAULT,
                                         D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED));
        if (d2d)
            d2d->CreateDCRenderTarget(&properties, target.GetAddressOf());
        observer = std::jthread([this](std::stop_token stop) {
            CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            ComPtr<IUIAutomation> automation;
            CoCreateInstance(CLSID_CUIAutomation, nullptr, CLSCTX_INPROC_SERVER,
                             IID_PPV_ARGS(automation.GetAddressOf()));
            auto events = Microsoft::WRL::Make<TaskbarEvents>();
            events->changed = [this] {
                observerDirty = true;
                observerWake.notify_all();
            };
            HWND attached{};
            while (!stop.stop_requested())
            {
                auto taskbar = FindWindowW(L"Shell_TrayWnd", nullptr);
                if (automation && taskbar != attached)
                {
                    automation->RemoveAllEventHandlers();
                    attached = nullptr;
                    ComPtr<IUIAutomationElement> root;
                    if (taskbar && SUCCEEDED(automation->ElementFromHandle(taskbar, root.GetAddressOf())))
                    {
                        PROPERTYID properties[] = {UIA_BoundingRectanglePropertyId};
                        HRESULT a = automation->AddPropertyChangedEventHandlerNativeArray(
                            root.Get(), TreeScope_Subtree, nullptr, events.Get(), properties, 1);
                        HRESULT b = automation->AddStructureChangedEventHandler(root.Get(), TreeScope_Subtree, nullptr,
                                                                                events.Get());
                        if (SUCCEEDED(a) && SUCCEEDED(b))
                            attached = taskbar;
                    }
                }
                observerDirty = false;
                measure(automation.Get());
                bool settling;
                {
                    std::lock_guard guard(lock);
                    settling = pending;
                }
                std::unique_lock waitLock(observerLock);
                observerWake.wait_for(waitLock, std::chrono::milliseconds(settling ? 80 : 500),
                                      [&] { return stop.stop_requested() || observerDirty.load(); });
            }
            if (automation)
                automation->RemoveAllEventHandlers();
            CoUninitialize();
        });
    }
    ~Impl()
    {
        observer.request_stop();
        observerWake.notify_all();
        if (observer.joinable())
            observer.join();
        if (window)
            DestroyWindow(window);
    }
    static VARIANT variant(int v)
    {
        VARIANT r{};
        r.vt = VT_I4;
        r.lVal = v;
        return r;
    }
    void measure(IUIAutomation *automation)
    {
        Json settings;
        bool previousRight;
        {
            std::lock_guard guard(lock);
            settings = config;
            previousRight = right;
        }
        TaskbarGeometry g{};
        auto taskbar = FindWindowW(L"Shell_TrayWnd", nullptr);
        RECT bar{};
        double scale = taskbar ? GetDpiForWindow(taskbar) / 96. : 1.;
        if (scale <= 0)
            scale = 1;
        bool autoLayout = settings.value("autoLayout", true);
        auto px = [&](double dip) { return static_cast<int>(std::ceil(dip * scale)); };
        int insetLeft = autoLayout ? px(8) : settings.value("leftMargin", 8),
            insetRight = autoLayout ? px(20) : settings.value("rightMargin", 40);
        bool available = taskbar && IsWindowVisible(taskbar) && GetWindowRect(taskbar, &bar) && bar.right > bar.left &&
                         bar.bottom > bar.top;
        g.available = available;
        g.left = bar.left;
        g.top = bar.top;
        g.right = bar.right;
        g.bottom = bar.bottom;
        if (available && bar.right - bar.left > bar.bottom - bar.top && automation)
        {
            ComPtr<IUIAutomationElement> root;
            if (SUCCEEDED(automation->ElementFromHandle(taskbar, root.GetAddressOf())))
            {
                ComPtr<IUIAutomationCondition> button, list, tab, either, condition;
                automation->CreatePropertyCondition(UIA_ControlTypePropertyId, variant(UIA_ButtonControlTypeId),
                                                    button.GetAddressOf());
                automation->CreatePropertyCondition(UIA_ControlTypePropertyId, variant(UIA_ListItemControlTypeId),
                                                    list.GetAddressOf());
                automation->CreatePropertyCondition(UIA_ControlTypePropertyId, variant(UIA_TabItemControlTypeId),
                                                    tab.GetAddressOf());
                automation->CreateOrCondition(button.Get(), list.Get(), either.GetAddressOf());
                automation->CreateOrCondition(either.Get(), tab.Get(), condition.GetAddressOf());
                ComPtr<IUIAutomationElementArray> elements;
                ComPtr<IUIAutomationCacheRequest> cache;
                automation->CreateCacheRequest(cache.GetAddressOf());
                if (cache)
                {
                    cache->put_TreeScope(TreeScope_Element);
                    cache->put_AutomationElementMode(AutomationElementMode_None);
                    for (auto property : {UIA_BoundingRectanglePropertyId, UIA_IsOffscreenPropertyId,
                                          UIA_AutomationIdPropertyId, UIA_ClassNamePropertyId, UIA_NamePropertyId})
                        cache->AddProperty(property);
                }
                if (condition && cache &&
                    SUCCEEDED(root->FindAllBuildCache(TreeScope_Descendants, condition.Get(), cache.Get(),
                                                      elements.GetAddressOf())))
                {
                    int count = 0;
                    elements->get_Length(&count);
                    int start = -1, tray = -1;
                    std::vector<Interval> occupied;
                    auto notify = FindWindowExW(taskbar, nullptr, L"TrayNotifyWnd", nullptr);
                    RECT tr{};
                    if (notify && GetWindowRect(notify, &tr) && tr.right > tr.left)
                        tray = tr.left;
                    bool consistent = true;
                    for (int i = 0; i < count; ++i)
                    {
                        ComPtr<IUIAutomationElement> e;
                        if (FAILED(elements->GetElement(i, e.GetAddressOf())))
                        {
                            consistent = false;
                            break;
                        }
                        RECT r{};
                        BOOL off = FALSE;
                        if (FAILED(e->get_CachedIsOffscreen(&off)) || FAILED(e->get_CachedBoundingRectangle(&r)))
                        {
                            consistent = false;
                            break;
                        }
                        if (off || r.right <= r.left || r.bottom <= bar.top || r.top >= bar.bottom ||
                            r.right <= bar.left || r.left >= bar.right)
                            continue;
                        auto id = propertyText(e.Get(), UIA_AutomationIdPropertyId),
                             cls = propertyText(e.Get(), UIA_ClassNamePropertyId),
                             name = propertyText(e.Get(), UIA_NamePropertyId);
                        if (id == L"StartButton" || id == L"Start" || name == L"开始" || name == L"開始" ||
                            name == L"Start" || name == L"スタート" || name == L"시작")
                            start = r.left;
                        if (cls.starts_with(L"SystemTray.") || id == L"SystemTrayIcon" || id == L"NotifyItemIcon")
                            tray = tray < 0 ? static_cast<int>(r.left) : std::min(tray, static_cast<int>(r.left));
                        occupied.push_back({std::max(bar.left, r.left), std::min(bar.right, r.right)});
                    }
                    if (consistent && start >= bar.left && tray >= start)
                    {
                        int cluster = start;
                        for (auto x : occupied)
                            if (x.left >= start && x.left < tray)
                                cluster = std::max(cluster, x.right);
                        g.start = start;
                        g.measured = true;
                        g.leftGap = gap(bar.left, start, occupied, insetLeft, insetRight);
                        g.rightGap = gap(std::min(cluster, tray), tray, occupied, insetLeft, insetRight);
                    }
                }
            }
        }
        RECT result{}, safeResult{};
        bool isRight = false, success = false;
        std::string side = "Hidden";
        if (available)
        {
            int minimum = autoLayout ? px(std::max(120., settings.value("fontSize", 14.) * 7))
                                     : std::max(80, settings.value("minWidth", 750));
            int maximum = autoLayout
                              ? std::max(minimum, std::min(px(900), static_cast<int>((bar.right - bar.left) * 4 / 10)))
                              : std::max(minimum, settings.value("maxWidth", 1350));
            if (g.measured)
            {
                auto p = stableChoose(g.leftGap, g.rightGap, minimum, maximum, previousRight,
                                      std::max(24, static_cast<int>((bar.bottom - bar.top) / 2)));
                if (p.width)
                {
                    result = {p.left, bar.top, p.left + p.width, bar.bottom};
                    isRight = p.right;
                    success = true;
                    side = isRight ? "Right" : "Left";
                    auto s = isRight ? g.rightGap : g.leftGap;
                    safeResult = {s.left, bar.top, s.right, bar.bottom};
                }
            }
            else if (bar.right - bar.left > bar.bottom - bar.top)
            {
                // Manual fallback is allowed only when measurement failed, never after
                // confirmed no space.
                double ratio = settings.value("fallbackWidthPercent", 60.);
                if (ratio > 1)
                    ratio /= 100.;
                ratio = std::clamp(ratio, .05, .69);
                int width = manualTaskbarWidth(static_cast<int>(bar.right-bar.left),minimum,maximum,insetLeft,insetRight,ratio);
                result = {bar.left + insetLeft, bar.top, bar.left + insetLeft + width, bar.bottom};
                safeResult = result;
                success = true;
                side = "Manual";
            }
        }
        double now = ticks();
        std::lock_guard guard(lock);
        const bool settled = stability.observe(g, now);
        geometry = g;
        safe = safeResult;
        bool changed = !equalRect(measured, result) || right != isRight || valid != success;
        pending = !settled && changed;
        if (settled)
        {
            measured = result;
            right = isRight;
            valid = success;
            dpi = scale;
            pending = false;
        }
        state = {{"measured", g.measured},
                 {"measurementSucceeded", g.measured},
                 {"manualWidthVisible", available && !g.measured},
                 {"side", side},
                 {"visible", success},
                 {"pending", pending},
                 {"left", result.left},
                 {"top", result.top},
                 {"width", result.right - result.left},
                 {"height", result.bottom - result.top},
                 {"dpiScale", scale},
                 {"leftAvailable", g.leftGap.right - g.leftGap.left},
                 {"rightAvailable", g.rightGap.right - g.rightGap.left}};
    }
    bool fullscreen(const RECT &bar) const
    {
        auto fg = GetForegroundWindow();
        RECT fr{};
        MONITORINFO mi{sizeof(mi)};
        if (!fg || fg == window || IsIconic(fg) || !GetWindowRect(fg, &fr))
            return false;
        wchar_t cls[128]{};
        GetClassNameW(fg, cls, 128);
        if (wcscmp(cls, L"Progman") == 0 || wcscmp(cls, L"WorkerW") == 0 || wcscmp(cls, L"Shell_TrayWnd") == 0)
            return false;
        HMONITOR monitor = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        POINT point{bar.left, bar.top};
        if (monitor != MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST) || !GetMonitorInfoW(monitor, &mi))
            return false;
        return fr.left <= mi.rcMonitor.left && fr.top <= mi.rcMonitor.top && fr.right >= mi.rcMonitor.right &&
               fr.bottom >= mi.rcMonitor.bottom;
    }
    void draw(const std::vector<Line> &lines, double position, const Json &settings)
    {
        RECT r, clip;
        bool alignRight, show, changing;
        double scale;
        {
            std::lock_guard guard(lock);
            bool layoutChanged = false;
            for (const auto *name : {"fontSize", "autoLayout", "leftMargin", "rightMargin", "minWidth", "maxWidth",
                                     "fallbackWidthPercent"})
                if (config.value(name, Json{}) != settings.value(name, Json{}))
                {
                    layoutChanged = true;
                    break;
                }
            config = settings;
            if (layoutChanged)
            {
                observerDirty = true;
                observerWake.notify_all();
            }
            r = measured;
            clip = safe;
            alignRight = right;
            show = valid;
            changing = pending;
            scale = dpi;
        }
        int current = resolvedLine(lines, position);
        std::wstring text = current >= 0    ? wide(lines[current].text)
                            : lines.empty() ? wide(settings.value("fallbackText", ""))
                                            : L"";
        if (current >= 0 && !lines[current].words.empty())
        {
            std::wstring joined;
            for (const auto &word : lines[current].words)
                joined += wide(word.text);
            if (!joined.empty())
                text = std::move(joined);
        }
        if (!settings.value("overlay", true) || !show || text.empty() || !target || !write || fullscreen(r))
        {
            ShowWindow(window, SW_HIDE);
            wasVisible = false;
            return;
        }
        BOOL clientAnimation = TRUE;
        SystemParametersInfoW(SPI_GETCLIENTAREAANIMATION, 0, &clientAnimation, 0);
        HIGHCONTRASTW contrast{sizeof(contrast)};
        SystemParametersInfoW(SPI_GETHIGHCONTRAST, sizeof(contrast), &contrast, 0);
        bool animate =
            settings.value("animations", true) && clientAnimation && !(contrast.dwFlags & HCF_HIGHCONTRASTON);
        double now = ticks();
        if (changing && wasVisible && (phase == 0 || phase == 3))
        {
            phase = 1;
            exitStarted = now;
        }
        if (!changing && (!equalRect(displayed, r) || displayedRight != alignRight || !wasVisible))
        {
            // Commit complete native geometry once. Drawing motion never resizes the
            // HWND.
            displayed = r;
            displayedRight = alignRight;
            phase = 3;
            entryStarted = now;
            pan = 0;
        }
        float translation = 0, opacity = 1;
        if (phase == 1)
        {
            double t = animate ? std::clamp((now - exitStarted) / 180., 0., 1.) : 1.;
            translation =
                static_cast<float>((displayedRight ? 1 : -1) * (displayed.right - displayed.left + 16) * t * t);
            opacity = static_cast<float>(1 - t);
            if (t >= 1)
                phase = 2;
        }
        else if (phase == 2)
        {
            ShowWindow(window, SW_HIDE);
            return;
        }
        else if (phase == 3)
        {
            double t = animate ? std::clamp((now - entryStarted) / 240., 0., 1.) : 1.;
            double ease = 1 - std::pow(1 - t, 3);
            translation =
                static_cast<float>((displayedRight ? 1 : -1) * (displayed.right - displayed.left + 16) * (1 - ease));
            opacity = static_cast<float>(ease);
            if (t >= 1)
                phase = 0;
        }
        r = displayed;
        alignRight = displayedRight;
        int w = r.right - r.left, h = r.bottom - r.top;
        if (w <= 0 || h <= 0)
            return;
        HDC screen = GetDC(nullptr), dc = CreateCompatibleDC(screen);
        BITMAPINFO bi{};
        bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        bi.bmiHeader.biWidth = w;
        bi.bmiHeader.biHeight = -h;
        bi.bmiHeader.biPlanes = 1;
        bi.bmiHeader.biBitCount = 32;
        bi.bmiHeader.biCompression = BI_RGB;
        void *bits{};
        HBITMAP bitmap = CreateDIBSection(screen, &bi, DIB_RGB_COLORS, &bits, nullptr, 0);
        if (!bitmap)
        {
            DeleteDC(dc);
            ReleaseDC(nullptr, screen);
            return;
        }
        auto old = SelectObject(dc, bitmap);
        memset(bits, 0, static_cast<size_t>(w) * h * 4);
        RECT local{0, 0, w, h};
        target->BindDC(dc, &local);
        target->BeginDraw();
        target->Clear(D2D1::ColorF(0, 0.f));
        target->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
        float fontSize = static_cast<float>(settings.value("fontSize", 14.) * scale);
        ComPtr<IDWriteTextFormat> format;
        auto family = wide(settings.value("font", "Microsoft YaHei UI"));
        auto layoutKey = utf8(text) + "|" + utf8(family) + "|" + std::to_string(fontSize) + "|" + std::to_string(w) +
                         "|" + std::to_string(h) + "|" + settings.value("fonts", Json::object()).dump();
        write->CreateTextFormat(family.c_str(), nullptr, DWRITE_FONT_WEIGHT_SEMI_BOLD, DWRITE_FONT_STYLE_NORMAL,
                                DWRITE_FONT_STRETCH_NORMAL, fontSize, L"zh-CN", format.GetAddressOf());
        if (format)
        {
            format->SetWordWrapping(DWRITE_WORD_WRAPPING_NO_WRAP);
            format->SetTextAlignment(DWRITE_TEXT_ALIGNMENT_LEADING);
            format->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_NEAR);
            ComPtr<IDWriteTextLayout> layout = cachedLayoutKey == layoutKey ? cachedLayout : nullptr;
            bool rebuild = !layout;
            if (rebuild)
                write->CreateTextLayout(text.c_str(), static_cast<UINT32>(text.size()), format.Get(),
                                        static_cast<float>(w), static_cast<float>(h), layout.GetAddressOf());
            if (rebuild && layout && settings.contains("fonts") && settings["fonts"].is_object())
            {
                auto fonts = settings["fonts"];
                for (UINT32 i = 0; i < text.size();)
                {
                    UINT32 length = 1;
                    char32_t cp = text[i];
                    if (cp >= 0xd800 && cp <= 0xdbff && i + 1 < text.size())
                    {
                        cp = 0x10000 + ((cp - 0xd800) << 10) + (text[i + 1] - 0xdc00);
                        length = 2;
                    }
                    auto script = scriptFamily(cp);
                    auto f = wide(fonts.value(script, settings.value("font", "Microsoft YaHei UI")));
                    layout->SetFontFamilyName(f.c_str(), {i, length});
                    i += length;
                }
            }
            if (rebuild && layout)
            {
                cachedLayout = layout;
                cachedLayoutKey = layoutKey;
            }
            ComPtr<ID2D1SolidColorBrush> normal, highlight, shadow;
            target->CreateSolidColorBrush(brushColor(settings.value("normal", "#FF496DBF")), normal.GetAddressOf());
            target->CreateSolidColorBrush(brushColor(settings.value("highlight", "#FFA0CCEE")),
                                          highlight.GetAddressOf());
            target->CreateSolidColorBrush(brushColor(settings.value("shadowColor", "#80000000")),
                                          shadow.GetAddressOf());
            if (layout && normal && highlight)
            {
                DWRITE_TEXT_METRICS metrics{};
                layout->GetMetrics(&metrics);
                float sung = 0;
                if (current >= 0)
                    for (auto &word : lines[current].words)
                        sung += static_cast<float>(
                            wide(word.text).size() *
                            std::clamp((position - word.start) / std::max(1., word.duration), 0., 1.));
                UINT32 index = std::min(static_cast<UINT32>(sung), static_cast<UINT32>(text.size()));
                float cursor = 0, y = 0;
                DWRITE_HIT_TEST_METRICS hit{};
                if (current >= 0 && !lines[current].words.empty() && index < text.size())
                {
                    layout->HitTestTextPosition(index, FALSE, &cursor, &y, &hit);
                    cursor += hit.width * (sung - std::floor(sung));
                }
                else if (current >= 0)
                    cursor = metrics.widthIncludingTrailingWhitespace *
                             static_cast<float>(progress(lines[current], position));
                if (text != previousText || current != lastLine)
                {
                    if (animate && wasVisible && !previousText.empty() && current >= 0 && lastLine >= 0)
                        lineStarted = now;
                    pan = 0;
                    previousText = text;
                    lastLine = current;
                }
                float overflow = std::max(0.f, metrics.widthIncludingTrailingWhitespace - w);
                if (overflow > 0 && settings.value("autoPan", true))
                {
                    float desired = std::clamp(cursor - w * .7f, 0.f, overflow);
                    if (current < 0 || lines[current].words.empty())
                        desired = 0; // no fabricated line timing
                    pan = animate ? pan + (desired - pan) * .18f : desired;
                }
                else
                    pan = 0;
                auto alignment = alignRight ? std::string("Right") : settings.value("alignment", "Center");
                float x = overflow > 0           ? -pan
                          : alignment == "Left"  ? 0
                          : alignment == "Right" ? w - metrics.widthIncludingTrailingWhitespace
                                                 : (w - metrics.widthIncludingTrailingWhitespace) / 2;
                auto origin =
                    D2D1::Point2F(x + translation, static_cast<float>((h - metrics.height) / 2 +
                                                                      settings.value("verticalOffset", 0.) * scale));
                // Intersect the old viewport with the latest safe free band during
                // shell motion.
                float left = static_cast<float>(std::clamp(clip.left - r.left, 0L, static_cast<LONG>(w))),
                      rightEdge = static_cast<float>(std::clamp(clip.right - r.left, 0L, static_cast<LONG>(w)));
                if (rightEdge < left)
                    rightEdge = left;
                target->PushAxisAlignedClip(D2D1::RectF(left, 0, rightEdge, static_cast<float>(h)),
                                            D2D1_ANTIALIAS_MODE_ALIASED);
                if (settings.value("shadow", true) && shadow)
                    target->DrawTextLayout(
                        D2D1::Point2F(origin.x + static_cast<float>(settings.value("shadowOffsetX", 1.) * scale),
                                      origin.y + static_cast<float>(settings.value("shadowOffsetY", 1.) * scale)),
                        layout.Get(), shadow.Get());
                target->DrawTextLayout(origin, layout.Get(), normal.Get());
                if (current >= 0 && settings.value("karaoke", true) && cursor > 0)
                {
                    target->PushAxisAlignedClip(D2D1::RectF(origin.x, 0, origin.x + cursor, static_cast<float>(h)),
                                                D2D1_ANTIALIAS_MODE_ALIASED);
                    target->DrawTextLayout(origin, layout.Get(), highlight.Get());
                    target->PopAxisAlignedClip();
                }
                target->PopAxisAlignedClip();
            }
        }
        HRESULT result = target->EndDraw();
        if (SUCCEEDED(result))
        {
            if (animate && now - lineStarted < 80)
                opacity *= static_cast<float>(.85 + .15 * std::clamp((now - lineStarted) / 80., 0., 1.));
            POINT dest{r.left, r.top}, src{};
            SIZE size{w, h};
            BLENDFUNCTION blend{AC_SRC_OVER, 0, static_cast<BYTE>(std::clamp(opacity, 0.f, 1.f) * 255), AC_SRC_ALPHA};
            UpdateLayeredWindow(window, screen, &dest, &size, dc, &src, 0, &blend, ULW_ALPHA);
            SetWindowPos(window, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            wasVisible = true;
        }
        SelectObject(dc, old);
        DeleteObject(bitmap);
        DeleteDC(dc);
        ReleaseDC(nullptr, screen);
    }
};
Overlay::Overlay() : impl(std::make_unique<Impl>())
{
}
Overlay::~Overlay() = default;
void Overlay::update(const std::vector<Line> &l, double p, const Json &s)
{
    impl->draw(l, p, s);
}
void Overlay::place()
{
}
void Overlay::hide()
{
    ShowWindow(impl->window, SW_HIDE);
    impl->wasVisible = false;
}
Json Overlay::layoutState() const
{
    std::lock_guard guard(impl->lock);
    return impl->state;
}
} // namespace lyrics
