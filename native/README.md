# TaskbarLyrics — C++ / React 版本

主界面已迁移到 React，播放器、歌词匹配、缓存、设置和任务栏显示由 C++20 实现。程序运行不启动旧 C# 程序，也不加载 .NET。主界面使用 Microsoft Edge WebView2 Runtime；任务栏歌词使用 Direct2D / DirectWrite。

## 页面与功能

保留现有的正在播放、外观、时间校准、设置和关于五页，沿用现有布局、配色、圆角与交互。包括滚动歌词和译文、媒体控制与拖动进度、自定义字体菜单、按语言字体、OKLCH 双色预设、自定义色相、色板和独立最近颜色、单曲和全局校准、数据统计与清理。

后端包括 GSMTC 事件和会话恢复、播放时钟及切歌状态稳定、多阶段自动匹配与版本评分、手动匹配优先、QRC 解密与逐字歌词、译文、本地缓存修复、任务栏空间测量与布局动画、全屏隐藏、DWM 歌词缩略图和媒体按钮、托盘、开机启动与日志轮转。日志保留最近 7 天，总量不超过 20 MB。

任务栏优先用左侧空白；左侧不足则用右侧并右对齐；两侧不足隐藏。只有测量失败时使用手动宽度。默认配色精确保留 `#FF496DBF` / `#FFA0CCEE`，当前字体选择会迁移，不打包个人自定义字体。

## 运行与数据

运行 `TaskbarLyrics.Native.exe`，保留同目录 `ui` 和 `licenses`。不需要 .NET，主界面需要 WebView2 Runtime。

便携版带 `portable.flag`，设置、缓存、日志和 WebView2 数据均在同目录 `Data`。安装版数据在 `%LOCALAPPDATA%\TaskbarLyricsNative`。首次启动会复制原版配置与缓存，原版数据保留；原版便携数据从上一级 `Data` 导入，其他情况从 `%LOCALAPPDATA%\TaskbarLyrics` 导入。开机启动项由新程序自己的注册表项控制。

公开源码只包含当前 C++ / React 实现，不依赖旧 C# 项目。请避免同时运行新旧两套任务栏歌词。

## 构建

需要 Visual Studio C++ 桌面开发组件、Windows SDK 和 Node.js。

```powershell
pwsh -NoProfile -File eng/build-native.ps1 -Restore
pwsh -NoProfile -File eng/package-native.ps1
```

窗口生命周期检查：先退出正在运行的新程序，再运行 `pwsh -NoProfile -File eng/test-native-window.ps1`。检查首次渲染、隐藏后重新打开以及释放 WebView 后的重建，并验证导航和主页面实际内容及容器尺寸；网页加载成功不能替代渲染检查。重复运行 EXE 与托盘打开窗口走同一恢复路径。

后续构建不需要 `-Restore`。输出位于 `publish/Release/Native`；安装包和便携 ZIP 位于 `publish/Release`。静态链接 MSVC CRT 和 WebView2 Loader，不捆绑浏览器运行时。

CTest 覆盖配色、字幕同步、任务栏布局、播放时钟、设置迁移、缓存隔离、匹配和 QRC。歌词回归包括 171 个匹配参考场景、12 个 DES/zlib 测试向量及缓存修复、手动优先、重试和取消流程。参考数据随源码提供，运行测试不需要旧版项目或真实个人数据。

Windows 的缩略图外框由系统控制；不同屏幕缩放、任务栏布局、播放器实现及 Explorer 重启仍应进行实机回归，自动化结果不代表所有系统组合都经过验证。

依赖许可证见 `licenses`。版本固定在 `package-lock.json` 和构建脚本中。
