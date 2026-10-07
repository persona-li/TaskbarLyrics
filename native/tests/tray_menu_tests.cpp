#include "tray_menu.hpp"
#include <iostream>
#include <stdexcept>
namespace lyrics {
struct TrayMenuTestAccess {
    static void run() {
        auto require = [](bool ok, const char *message) { if (!ok) throw std::runtime_error(message); };
        WNDCLASSW cls{};
        cls.hInstance = GetModuleHandleW(nullptr);
        cls.lpfnWndProc = DefWindowProcW;
        cls.lpszClassName = L"TrayTestOwner";
        RegisterClassW(&cls);
        HWND owner = CreateWindowW(cls.lpszClassName, L"", WS_POPUP, 0, 0, 200, 200,
                                  nullptr, nullptr, cls.hInstance, nullptr);
        require(owner != nullptr, "test owner created");
        try {
            TrayMenu menu(owner, Snapshot{}, defaultSettings(), L"等待播放", false, [] {});
            cls.lpfnWndProc = TrayMenu::procedure;
            cls.lpszClassName = L"TrayTestPopup";
            RegisterClassW(&cls);
            menu.popup = CreateWindowW(cls.lpszClassName, L"", WS_POPUP, 10, 10, menu.width, menu.height,
                                       owner, nullptr, cls.hInstance, &menu);
            require(menu.popup != nullptr, "test popup created");
            require(SendMessageW(menu.popup, WM_MOUSEACTIVATE, reinterpret_cast<WPARAM>(owner),
                                 MAKELPARAM(HTCLIENT, WM_LBUTTONDOWN)) == MA_NOACTIVATE,
                    "menu interaction must not take focus from Explorer's overflow flyout");
            SendMessageW(menu.popup, WM_ACTIVATE, WA_INACTIVE, 0);
            require(!menu.done, "focus loss must not close tray menu");
            SendMessageW(menu.popup, WM_MOUSEMOVE, 0, MAKELPARAM(30, 30));
            SendMessageW(menu.popup, WM_MOUSELEAVE, 0, 0);
            require(!menu.done, "mouse hover/leave must not close tray menu");
            TrayMenu::activeMenu = &menu;
            menu.openedAt = GetTickCount64() - 250;
            RECT bounds{}; GetWindowRect(menu.popup, &bounds);
            MSLLHOOKSTRUCT mouse{}; mouse.pt = {bounds.left + 30, bounds.top + 30};
            MSG message{};
            TrayMenu::outsideMouse(HC_ACTION, WM_LBUTTONDOWN, reinterpret_cast<LPARAM>(&mouse));
            require(!PeekMessageW(&message, menu.popup, WM_CLOSE, WM_CLOSE, PM_REMOVE), "inside click stays open");
            mouse.pt = {bounds.right + 20, bounds.bottom + 20};
            TrayMenu::outsideMouse(HC_ACTION, WM_MOUSEMOVE, reinterpret_cast<LPARAM>(&mouse));
            require(!PeekMessageW(&message, menu.popup, WM_CLOSE, WM_CLOSE, PM_REMOVE), "outside motion stays open");
            TrayMenu::outsideMouse(HC_ACTION, WM_LBUTTONDOWN, reinterpret_cast<LPARAM>(&mouse));
            require(PeekMessageW(&message, menu.popup, WM_CLOSE, WM_CLOSE, PM_REMOVE) != FALSE, "outside click posts close");
            DispatchMessageW(&message);
            require(menu.done, "outside click closes menu");
        } catch (...) { DestroyWindow(owner); throw; }
        DestroyWindow(owner);
    }
};
}
int main() {
    try { lyrics::TrayMenuTestAccess::run(); std::cout << "PASS: tray focus, hover, inside/outside click dismissal\n"; }
    catch (const std::exception &e) { std::cerr << e.what() << '\n'; return 1; }
}
