#pragma once
#include "settings.hpp"
#include <gdiplus.h>
#include <shellapi.h>
#include <windowsx.h>
namespace lyrics {
// Own the full menu surface to avoid the system HMENU's extra gutters and
// borders.
class TrayMenu {
  friend struct TrayMenuTestAccess;
  struct Item {
    std::wstring text;
    int command{}, top{}, height{};
    bool secondary{}, disabled{}, separator{};
  };
  std::vector<Item> items;
  HWND popup{}, owner{};
  HHOOK outsideHook{};
  ULONGLONG openedAt{};
  inline static TrayMenu *activeMenu{};
  HFONT font{};
  int dpi{96}, width{}, height{}, hover{-1}, result{};
  bool dark{}, checked{}, done{};
  std::function<void()> toggle;
  static LRESULT CALLBACK outsideMouse(int code, WPARAM message, LPARAM data) {
    auto menu = activeMenu;
    if (code >= 0 && menu && menu->popup && GetTickCount64() - menu->openedAt >= 200 &&
        (message == WM_LBUTTONDOWN || message == WM_RBUTTONDOWN || message == WM_MBUTTONDOWN)) {
      auto point = reinterpret_cast<MSLLHOOKSTRUCT *>(data)->pt;
      RECT bounds{};
      if (GetWindowRect(menu->popup, &bounds) && !PtInRect(&bounds, point))
        PostMessageW(menu->popup, WM_CLOSE, 0, 0);
    }
    // Observe only: the outside click must still reach its original target.
    return CallNextHookEx(nullptr, code, message, data);
  }
  void releaseHook() {
    if (outsideHook) { UnhookWindowsHookEx(outsideHook); outsideHook = nullptr; }
    if (activeMenu == this) activeMenu = nullptr;
  }
  int px(int value) const { return MulDiv(value, dpi, 96); }
  Gdiplus::Color colour(unsigned light, unsigned night) const {
    return Gdiplus::Color(0xff000000u | (dark ? night : light));
  }
  static void rounded(Gdiplus::GraphicsPath &path, float x, float y, float w,
                      float h, float radius) {
    float d = std::min(radius * 2, std::min(w, h));
    path.AddArc(x, y, d, d, 180, 90);
    path.AddArc(x + w - d, y, d, d, 270, 90);
    path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
    path.AddArc(x, y + h - d, d, d, 90, 90);
    path.CloseFigure();
  }
  void paint(HDC target) {
    auto dc = CreateCompatibleDC(target);
    auto bitmap = CreateCompatibleBitmap(target, width, height);
    auto oldBitmap = SelectObject(dc, bitmap);
    {
      Gdiplus::Graphics g(dc);
      g.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
      g.Clear(colour(0xffffff, 0x18181b));
      Gdiplus::GraphicsPath border;
      rounded(border, .5f, .5f, float(width - 1), float(height - 1),
              float(px(8)));
      Gdiplus::Pen outline(colour(0xc6c6cb, 0x56565c), float(dpi) / 96.f);
      g.DrawPath(&outline, &border);
      for (size_t i = 0; i < items.size(); ++i) {
        auto &row = items[i];
        if (row.separator) {
          Gdiplus::Pen pen(colour(0xe6e6e9, 0x38383c), float(dpi) / 96.f);
          float y = float(row.top + row.height / 2);
          g.DrawLine(&pen, float(px(16)), y, float(width - px(16)), y);
          continue;
        }
        if (static_cast<int>(i) == hover && row.command && !row.disabled) {
          Gdiplus::GraphicsPath pill;
          rounded(pill, float(px(4)), float(row.top + px(1)),
                  float(width - px(8)), float(row.height - px(2)),
                  float(px(8)));
          Gdiplus::SolidBrush brush(colour(0xeaf3ff, 0x2a4261));
          g.FillPath(&brush, &pill);
        }
        if (row.command == 3 && checked) {
          Gdiplus::Pen pen(colour(0x0065ce, 0x93bcff), 1.6f * dpi / 96.f);
          pen.SetStartCap(Gdiplus::LineCapRound);
          pen.SetEndCap(Gdiplus::LineCapRound);
          float x = float(px(12)), y = float(row.top + row.height / 2);
          Gdiplus::PointF points[] = {
              {x - px(4), y}, {x - px(1), y + px(3)}, {x + px(5), y - px(4)}};
          g.DrawLines(&pen, points, 3);
        }
      }
    }
    auto oldFont = SelectObject(dc, font);
    SetBkMode(dc, TRANSPARENT);
    for (auto &row : items) {
      if (row.separator)
        continue;
      bool secondary = row.secondary || row.disabled;
      SetTextColor(dc, secondary
                           ? (dark ? RGB(192, 192, 197) : RGB(89, 89, 94))
                           : (dark ? RGB(245, 245, 247) : RGB(29, 29, 31)));
      RECT rect{px(22), row.top, width - px(22), row.top + row.height};
      DrawTextW(dc, row.text.c_str(), static_cast<int>(row.text.size()), &rect,
                DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS |
                    DT_NOPREFIX);
    }
    SelectObject(dc, oldFont);
    BitBlt(target, 0, 0, width, height, dc, 0, 0, SRCCOPY);
    SelectObject(dc, oldBitmap);
    DeleteObject(bitmap);
    DeleteDC(dc);
  }
  int hit(int x, int y) const {
    if (x < 0 || x >= width)
      return -1;
    for (size_t i = 0; i < items.size(); ++i)
      if (y >= items[i].top && y < items[i].top + items[i].height &&
          items[i].command && !items[i].disabled)
        return static_cast<int>(i);
    return -1;
  }
  static LRESULT CALLBACK procedure(HWND h, UINT message, WPARAM w, LPARAM l) {
    auto self =
        reinterpret_cast<TrayMenu *>(GetWindowLongPtrW(h, GWLP_USERDATA));
    if (message == WM_NCCREATE) {
      self = static_cast<TrayMenu *>(
          reinterpret_cast<CREATESTRUCTW *>(l)->lpCreateParams);
      SetWindowLongPtrW(h, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
    }
    if (!self)
      return DefWindowProcW(h, message, w, l);
    switch (message) {
    case WM_ERASEBKGND:
      return 1;
    case WM_PAINT: {
      PAINTSTRUCT paint{};
      auto dc = BeginPaint(h, &paint);
      self->paint(dc);
      EndPaint(h, &paint);
      return 0;
    }
    case WM_MOUSEMOVE: {
      int next = self->hit(GET_X_LPARAM(l), GET_Y_LPARAM(l));
      if (next != self->hover) {
        self->hover = next;
        InvalidateRect(h, nullptr, FALSE);
      }
      TRACKMOUSEEVENT tracking{sizeof(tracking), TME_LEAVE, h, 0};
      TrackMouseEvent(&tracking);
      return 0;
    }
    case WM_MOUSELEAVE:
      self->hover = -1;
      InvalidateRect(h, nullptr, FALSE);
      return 0;
    case WM_LBUTTONUP:
    case WM_RBUTTONUP: {
      int index = self->hit(GET_X_LPARAM(l), GET_Y_LPARAM(l));
      if (index >= 0) {
        int command = self->items[index].command;
        if (command == 3) {
          self->toggle();
          self->checked = !self->checked;
          InvalidateRect(h, nullptr, FALSE);
        } else {
          self->result = command;
          self->done = true;
        }
      }
      return 0;
    }
    case WM_ACTIVATE:
      // Shell hover/flyout changes may deactivate us without an outside click.
      // Dismissal is owned by the mouse hook, icon toggle, or menu command.
      return 0;
    case WM_MOUSEACTIVATE:
      // Keep Explorer's overflow flyout active while interacting with our menu.
      // Accept the click without activating this tool window.
      return MA_NOACTIVATE;
    case WM_KEYDOWN:
    case WM_SYSKEYDOWN:
    case WM_CHAR:
      return 0;
    case WM_CLOSE:
      self->done = true;
      return 0;
    }
    return DefWindowProcW(h, message, w, l);
  }

public:
  TrayMenu(HWND window, const Snapshot &s, const Json &settings,
           std::wstring status, bool loading, std::function<void()> onToggle)
      : owner(window), toggle(std::move(onToggle)) {
    dpi = static_cast<int>(GetDpiForWindow(window));
    dark = settings.value("theme", std::string("system")) == "dark" ||
           (settings.value("theme", std::string("system")) == "system" &&
            !systemLight());
    checked = settings.value("overlay", true);
    // Original tray font: Microsoft YaHei UI, 9.25 pt.
    font = CreateFontW(-MulDiv(925, dpi, 7200), 0, 0, 0, FW_NORMAL, FALSE,
                       FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS,
                       CLIP_DEFAULT_PRECIS, CLEARTYPE_QUALITY, DEFAULT_PITCH,
                       L"Microsoft YaHei UI");
    auto title = wide(s.title);
    if (title.size() > 18)
      title = title.substr(0, 18) + L"…";
    auto add = [&](std::wstring text, int command = 0, bool secondary = false,
                   bool disabled = false) {
      items.push_back({std::move(text), command, 0, px(command ? 31 : 24),
                       secondary, disabled, false});
    };
    auto separator = [&] {
      items.push_back({L"", 0, 0, px(10), false, false, true});
    };
    add(title.empty() ? L"TaskbarLyrics" : title);
    add(std::move(status), 0, true);
    separator();
    add(L"显示任务栏歌词", 3);
    separator();
    add(L"打开窗口", 1);
    add(L"选择歌词", 5, false, s.title.empty() || loading);
    separator();
    add(L"退出", 2);
    auto dc = GetDC(window);
    auto old = SelectObject(dc, font);
    int textWidth = 0;
    height = px(6);
    for (auto &row : items) {
      row.top = height;
      height += row.height;
      SIZE size{};
      GetTextExtentPoint32W(dc, row.text.c_str(),
                            static_cast<int>(row.text.size()), &size);
      textWidth = std::max(textWidth, static_cast<int>(size.cx));
    }
    height += px(6);
    width = textWidth + px(56);
    SelectObject(dc, old);
    ReleaseDC(window, dc);
  }
  ~TrayMenu() {
    releaseHook();
    if (popup)
      DestroyWindow(popup);
    DeleteObject(font);
  }
  int show(HWND, POINT point) {
    Gdiplus::GdiplusStartupInput input;
    ULONG_PTR token{};
    if (Gdiplus::GdiplusStartup(&token, &input, nullptr) != Gdiplus::Ok)
      return 0;
    WNDCLASSW cls{};
    cls.hInstance = GetModuleHandleW(nullptr);
    cls.lpfnWndProc = procedure;
    cls.lpszClassName = L"TaskbarLyricsNativeTrayMenu";
    cls.hCursor = LoadCursorW(nullptr, IDC_ARROW);
    cls.style = CS_DROPSHADOW;
    RegisterClassW(&cls);
    NOTIFYICONIDENTIFIER icon{sizeof(icon)};
    icon.hWnd = owner;
    icon.uID = 1;
    RECT iconBounds{};
    if (SUCCEEDED(Shell_NotifyIconGetRect(&icon, &iconBounds)))
      point = {iconBounds.right, iconBounds.top};
    MONITORINFO monitor{sizeof(monitor)};
    GetMonitorInfoW(MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST),
                    &monitor);
    width = std::min(
        width, static_cast<int>(monitor.rcWork.right - monitor.rcWork.left));
    int x = std::clamp(point.x - width, monitor.rcWork.left,
                       monitor.rcWork.right - width);
    int y = std::clamp(point.y - height, monitor.rcWork.top,
                       monitor.rcWork.bottom - height);
    popup = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE, cls.lpszClassName,
                            L"", WS_POPUP, x, y, width, height, owner, nullptr,
                            cls.hInstance, this);
    if (popup) {
      SetWindowRgn(
          popup,
          CreateRoundRectRgn(0, 0, width + 1, height + 1, px(16), px(16)),
          FALSE);
      ShowWindow(popup, SW_SHOWNOACTIVATE);
      openedAt = GetTickCount64();
      activeMenu = this;
      outsideHook = SetWindowsHookExW(WH_MOUSE_LL, outsideMouse, GetModuleHandleW(nullptr), 0);
      MSG message{};
      while (!done) {
        int read = GetMessageW(&message, nullptr, 0, 0);
        if (read <= 0) {
          if (read == 0)
            PostQuitMessage(static_cast<int>(message.wParam));
          break;
        }
        TranslateMessage(&message);
        DispatchMessageW(&message);
      }
      releaseHook();
      DestroyWindow(popup);
      popup = nullptr;
    }
    Gdiplus::GdiplusShutdown(token);
    return result;
  }
  static bool measure(MEASUREITEMSTRUCT *) { return false; }
  static bool draw(DRAWITEMSTRUCT *) { return false; }
};
} // namespace lyrics
