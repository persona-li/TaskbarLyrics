#pragma once
#include "settings.hpp"
#include <d2d1.h>
#include <dwmapi.h>
#include <dwrite.h>
#include <shobjidl.h>
namespace lyrics
{
class Thumbnail
{
    HWND window{};
    ComPtr<ITaskbarList3> taskbar;
    ComPtr<ID2D1Factory> factory;
    ComPtr<IDWriteFactory> write;
    ComPtr<ID2D1DCRenderTarget> target;
    std::string contentKey, buttonKey;
    std::array<HICON, 3> icons{};
    bool added{};
    HBITMAP bitmap(int width, int height, const std::function<void()> &draw)
    {
        BITMAPINFO bi{};
        bi.bmiHeader = {sizeof(BITMAPINFOHEADER), width, -height, 1, 32, BI_RGB};
        void *pixels{};
        auto b = CreateDIBSection(nullptr, &bi, DIB_RGB_COLORS, &pixels, nullptr, 0);
        if (!b || !pixels || !target)
            return b;
        auto dc = CreateCompatibleDC(nullptr);
        auto old = SelectObject(dc, b);
        RECT bounds{0, 0, width, height};
        target->BindDC(dc, &bounds);
        target->BeginDraw();
        target->Clear(D2D1::ColorF(0, 0));
        target->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
        draw();
        target->EndDraw();
        SelectObject(dc, old);
        DeleteDC(dc);
        return b;
    }
    void text(const std::wstring &value, float size, const D2D1_RECT_F &rect, D2D1_COLOR_F color, bool bold = false)
    {
        ComPtr<IDWriteTextFormat> format;
        ComPtr<ID2D1SolidColorBrush> brush;
        write->CreateTextFormat(
            L"Microsoft YaHei UI", nullptr, bold ? DWRITE_FONT_WEIGHT_SEMI_BOLD : DWRITE_FONT_WEIGHT_NORMAL,
            DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, size, L"zh-cn", format.GetAddressOf());
        if (!format)
            return;
        DWRITE_TRIMMING trim{DWRITE_TRIMMING_GRANULARITY_CHARACTER};
        ComPtr<IDWriteInlineObject> ellipsis;
        write->CreateEllipsisTrimmingSign(format.Get(), ellipsis.GetAddressOf());
        format->SetTrimming(&trim, ellipsis.Get());
        target->CreateSolidColorBrush(color, brush.GetAddressOf());
        target->DrawText(value.c_str(), static_cast<UINT32>(value.size()), format.Get(), rect, brush.Get(),
                         D2D1_DRAW_TEXT_OPTIONS_CLIP);
    }
    HICON icon(int index, bool playing)
    {
        auto b = bitmap(32, 32, [&] {
            // Match the UI's 18-unit filled transport geometry at icon resolution.
            target->SetTransform(D2D1::Matrix3x2F::Scale(32.f / 18.f, 32.f / 18.f));
            ComPtr<ID2D1SolidColorBrush> brush;
            target->CreateSolidColorBrush(D2D1::ColorF(.424f, .569f, .796f), brush.GetAddressOf());
            if (index == 1 && playing)
            {
                target->FillRectangle(D2D1::RectF(5, 3, 8, 15), brush.Get());
                target->FillRectangle(D2D1::RectF(10, 3, 13, 15), brush.Get());
                return;
            }
            if (index != 1)
                target->FillRectangle(index == 0 ? D2D1::RectF(3, 3, 5, 15) : D2D1::RectF(13, 3, 15, 15), brush.Get());
            ComPtr<ID2D1PathGeometry> geometry;
            ComPtr<ID2D1GeometrySink> sink;
            factory->CreatePathGeometry(geometry.GetAddressOf());
            geometry->Open(sink.GetAddressOf());
            auto x = index == 0 ? 15.f : index == 1 ? 5.f : 3.f;
            auto tip = index == 0 ? 7.f : index == 1 ? 16.f : 11.f;
            sink->BeginFigure(D2D1::Point2F(x, 3), D2D1_FIGURE_BEGIN_FILLED);
            sink->AddLine(D2D1::Point2F(tip, 9));
            sink->AddLine(D2D1::Point2F(x, 15));
            sink->EndFigure(D2D1_FIGURE_END_CLOSED);
            sink->Close();
            target->FillGeometry(geometry.Get(), brush.Get());
        });
        const unsigned char maskBits[128]{};
        auto mask = CreateBitmap(32, 32, 1, 1, maskBits);
        ICONINFO info{TRUE, 0, 0, mask, b};
        auto result = CreateIconIndirect(&info);
        DeleteObject(mask);
        DeleteObject(b);
        return result;
    }

  public:
    explicit Thumbnail(HWND h) : window(h)
    {
        D2D1CreateFactory(D2D1_FACTORY_TYPE_SINGLE_THREADED, factory.GetAddressOf());
        DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory),
                            reinterpret_cast<IUnknown **>(write.GetAddressOf()));
        auto properties =
            D2D1::RenderTargetProperties(D2D1_RENDER_TARGET_TYPE_DEFAULT,
                                         D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED));
        if (factory)
            factory->CreateDCRenderTarget(&properties, target.GetAddressOf());
        CoCreateInstance(CLSID_TaskbarList, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(taskbar.GetAddressOf()));
        if (taskbar)
            taskbar->HrInit();
        int yes = 1;
        for (auto attr : {DWMWA_FORCE_ICONIC_REPRESENTATION, DWMWA_HAS_ICONIC_BITMAP, DWMWA_DISALLOW_PEEK})
            DwmSetWindowAttribute(window, attr, &yes, sizeof(yes));
    }
    ~Thumbnail()
    {
        for (auto i : icons)
            if (i)
                DestroyIcon(i);
    }
    void explorerRestart()
    {
        added = false;
        buttonKey.clear();
        contentKey.clear();
    }
    void update(const Snapshot &s, const Document &doc, double position)
    {
        int index = resolvedLine(doc.lines, position);
        std::string textKey =
            s.key() + "|" + std::to_string(index) + "|" + std::to_string(systemLight(L"SystemUsesLightTheme"));
        if (index >= 0)
            textKey += doc.lines[index].text + doc.lines[index].translation;
        if (textKey != contentKey)
        {
            contentKey = textKey;
            DwmInvalidateIconicBitmaps(window);
        }
        auto key = std::to_string(s.presentationPlaying) + std::to_string(s.previous) + std::to_string(s.toggle) +
                   std::to_string(s.next);
        if (key == buttonKey || !taskbar)
            return;
        THUMBBUTTON buttons[3]{};
        for (int i = 0; i < 3; ++i)
        {
            if (icons[i])
                DestroyIcon(icons[i]);
            icons[i] = icon(i, s.presentationPlaying);
            buttons[i].dwMask = THB_ICON | THB_FLAGS | THB_TOOLTIP;
            buttons[i].iId = 100 + i;
            buttons[i].hIcon = icons[i];
            buttons[i].dwFlags = (i == 0 ? s.previous : i == 1 ? s.toggle : s.next) ? THBF_ENABLED : THBF_DISABLED;
            wcscpy_s(buttons[i].szTip, i == 0                  ? L"上一首"
                                       : i == 2                ? L"下一首"
                                       : s.presentationPlaying ? L"暂停"
                                                               : L"播放");
        }
        auto hr = added ? taskbar->ThumbBarUpdateButtons(window, 3, buttons)
                        : taskbar->ThumbBarAddButtons(window, 3, buttons);
        if (SUCCEEDED(hr))
        {
            added = true;
            buttonKey = key;
        }
    }
    HRESULT render(int maxWidth, int maxHeight, const Snapshot &s, const Document &doc, double position)
    {
        if (!target || !write || maxWidth < 1 || maxHeight < 1)
            return E_FAIL;
        auto dpi = GetDpiForWindow(window) / 96.;
        double scale = std::min({dpi, maxWidth / 180., maxHeight / 150.});
        int width = std::max(1, static_cast<int>(180 * scale)), height = std::max(1, static_cast<int>(150 * scale));
        auto light = systemLight(L"SystemUsesLightTheme");
        auto fg = D2D1::ColorF(light ? .114f : .94f, light ? .114f : .94f, light ? .122f : .95f);
        auto muted = D2D1::ColorF(light ? .35f : .72f, light ? .35f : .72f, light ? .37f : .75f);
        int index = resolvedLine(doc.lines, position);
        auto b = bitmap(width, height, [&] {
            target->SetTransform(D2D1::Matrix3x2F::Scale(static_cast<float>(scale), static_cast<float>(scale)));
            auto title = s.title.empty() ? L"等待播放" : wide(s.title + (s.artist.empty() ? "" : " · " + s.artist));
            text(title, 13, D2D1::RectF(16, 8, 164, 26), fg);
            auto original = index >= 0 ? wide(doc.lines[index].text)
                                       : wide(s.title.empty()     ? "在播放器中播放歌曲"
                                              : doc.instrumental  ? "♪ 纯音乐"
                                              : doc.lines.empty() ? s.title
                                                                  : "♪ 间奏");
            text(original, 20, D2D1::RectF(16, 32, 164, 108), D2D1::ColorF(.424f, .569f, .796f), true);
            if (index >= 0)
                text(wide(doc.lines[index].translation), 13, D2D1::RectF(16, 111, 164, 145), muted);
        });
        auto hr = b ? DwmSetIconicThumbnail(window, b, 0) : E_FAIL;
        if (b)
            DeleteObject(b);
        return hr;
    }
};
} // namespace lyrics
