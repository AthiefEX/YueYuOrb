<div align="center">

# 月语悬浮球

**把快捷启动、AI 网页、记事本、截图、键盘布局规则和实时翻译留在桌面指尖。**

<img src="assets/banner.webp" alt="月语悬浮球 — 把常用工具留在指尖" width="100%">

[![License: GPL-3.0-only](https://img.shields.io/badge/License-GPLv3-c9a961.svg)](./LICENSE)
[![Windows 10/11 x64](https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-2b7a78.svg)](#系统要求)
[![Release v1.3.0](https://img.shields.io/badge/Release-v1.3.0-d8b46a.svg)](https://github.com/AthiefEX/YueYuOrb/releases/tag/v1.3.0)

</div>

## 这是什么

月语悬浮球是一个面向 Windows 10/11 x64 的桌面悬浮助手。它常驻托盘，用一个可拖动的悬浮球组织高频工具，并把 AI 网页、本地笔记、截图标注、英文键盘布局规则和实时翻译收进同一个轻量入口。

它解决的不是“再打开一个管理后台”，而是减少窗口切换：需要启动、记录、截图或切换到专注网页时，操作从一个始终可触达的悬浮球开始，结束时仍回到原来的工作现场。

## 你会得到什么

<img src="assets/features.webp" alt="月语悬浮球核心体验：随手展开、专注网页、本地记录" width="100%">

点击悬浮球后，轮盘菜单自下而上扇形展开，常用应用与内置工具在同一个可滚动轮盘中访问。AI 网页在独立 WebView2 窗口中运行，可跟随月语悬浮球的浅色或深色外观。笔记、截图、自定义悬浮球图片和窗口规则默认保存在本机，不要求注册账号。

> 上方为品牌概念图，实际界面以当前发布包为准。

## 快速开始

v1.3.0 新增多屏亮度调节与黑夜模式，优化设置导航、AI 网页和记事本布局，并支持浮动面板外部点击关闭、拖动与位置记忆。详见 [本次更新说明](docs/release-notes-v1.3.0.md)。

前往 [GitHub Releases](https://github.com/AthiefEX/YueYuOrb/releases/tag/v1.3.0) 下载：

| 方式 | 文件 | 使用说明 |
|---|---|---|
| 便携版 | [YueYuOrb-1.3.0-win-x64-portable.zip](https://github.com/AthiefEX/YueYuOrb/releases/download/v1.3.0/YueYuOrb-1.3.0-win-x64-portable.zip) | 解压后运行 `YueYuOrb.exe`，不需要安装 |
| 安装版 | [YueYuOrb-1.3.0-win-x64-setup.exe](https://github.com/AthiefEX/YueYuOrb/releases/download/v1.3.0/YueYuOrb-1.3.0-win-x64-setup.exe) | 当前用户安装，包含开始菜单入口和可选桌面快捷方式 |
| 校验值 | [SHA256SUMS.txt](https://github.com/AthiefEX/YueYuOrb/releases/download/v1.3.0/SHA256SUMS.txt) | 下载后核对便携包和安装包 |

发行包为 .NET 10 self-contained，不要求目标机器安装 .NET SDK 或 .NET Desktop Runtime。安装包尚未进行代码签名，Windows SmartScreen 可能显示“未知发布者”警告。

## 主要能力

- **悬浮球与轮盘**：启动时回到主屏幕工作区，支持拖动、边缘吸附、位置锁定、无底板时间显示、自定义 PNG/JPEG/BMP 图片手动裁切和自下而上的扇形展开动画。
- **快捷启动**：拖入 exe 或快捷方式自动添加并识别真实图标，也可手动添加文件夹、设置参数与工作目录；统一轮盘支持滚轮浏览，可单独隐藏轮盘下方快捷应用栏。
- **AI 网页**：独立 WebView2 窗口，支持自定义 HTTPS 站点、DeepSeek、浅色/深色/跟随应用、缩放、固定、外部浏览器打开和登录 profile 保留。
- **本地记事本**：SQLite 存储、500 ms 自动保存、正文搜索、回收站与恢复、TXT 导出。
- **截图与中文标注**：框选、当前窗口、当前屏幕、倒计时、矩形/箭头/中文文字、撤销重做、PNG/JPEG、剪贴板和自动另存。
- **键盘布局规则**：按应用将前台窗口请求切换到已安装的英文非 IME 键盘，拦截常见切换快捷键，并在停止锁定或离开目标应用后恢复原布局。
- **实时翻译**：支持翻译选中文字、框选 OCR 翻译和固定区域视频字幕翻译；默认提供 MyMemory 免费服务，也可配置 Azure Translator、DeepL、LibreTranslate 或 OpenAI 兼容接口。
- **桌面集成**：托盘、可配置热键、开机启动、单实例、跨会话数据锁和设置备份。
- **亮度与黑夜模式**：同时调节支持的屏幕亮度，提供目标值和增减快捷键；可叠加自定义程度的软件降暗。

## 系统要求

- Windows 10 21H2/22H2 或 Windows 11，x64。
- 普通用户权限即可启动；不承诺控制提权应用、UAC/锁屏安全桌面或受保护内容。
- AI 网页需要 Microsoft Edge WebView2 Runtime。缺失时，悬浮球、笔记、截图和其他本地功能仍可使用。

## 数据与隐私

用户数据保存在 `%LOCALAPPDATA%\FloatDesk`，这是为兼容旧版本保留的固定数据目录：

```text
settings.json / settings.json.bak
data/notes.db
assets/orb.png
launcher-icons/
WebView2/
logs/
```

设置页支持导入与导出偏好，导入不会运行任何快捷项。翻译截图与 OCR 位图不会上传，只有选中文字或识别文字会发送给用户选择的翻译服务；翻译密钥使用 Windows DPAPI 当前用户范围加密，不写入设置文件。日志不记录笔记正文、截图内容、翻译原文或译文、按键、Cookie 或完整网页查询。月语悬浮球不会把本地数据自动上传到自己的服务器。

## 从源码构建

需要 .NET SDK `10.0.400`：

```powershell
git clone https://github.com/AthiefEX/YueYuOrb.git
cd YueYuOrb
dotnet restore FloatDesk.sln --locked-mode
dotnet build FloatDesk.sln -c Release --no-restore
.\scripts\test.ps1 -Configuration Release
.\scripts\publish.ps1 -Version 1.3.0 -Installer
```

项目内部保留 `FloatDesk.*` 命名空间和解决方案名以兼容现有代码与设置，正式产品名、程序集和可执行文件为月语悬浮球、`YueYuOrb.dll` 和 `YueYuOrb.exe`。

## 验证与已知边界

当前 Release 自动化测试为 **872/872 通过**。已运行只读设备枚举、亮度模拟及窗口交互测试；未更改真实设备亮度和音量，也未执行覆盖安装冒烟。以下项目仍需在对应设备上人工验收：

- Windows 11、Windows 10 21H2、标准用户干净虚拟机。
- 真实第三方输入法、自定义切换快捷键与提权目标窗口。
- DeepSeek 登录与完整问答流程。
- 真实翻译服务凭据、额度、网络延迟与所有 Windows OCR 语言包。
- 混合 DPI、多屏旋转、HDR、独占全屏和游戏覆盖。
- 未签名安装包的长期升级与 SmartScreen 体验。

详细说明见 [v1.3.0 更新说明](docs/release-notes-v1.3.0.md)、[用户手册](docs/user-guide.md)、[架构说明](docs/architecture.md)、[兼容性报告](docs/compatibility.md) 和 [手工验收记录](docs/manual-tests.md)。

## 开源许可

月语悬浮球使用 [GNU General Public License v3.0 only](./LICENSE) 开源。

作者：**七月风语**<br>
GitHub：[AthiefEX](https://github.com/AthiefEX)<br>
项目：[AthiefEX/YueYuOrb](https://github.com/AthiefEX/YueYuOrb)
