# 月语悬浮球 v1.0.0

首个公开测试发行版，面向 Windows 10/11 x64。

月语悬浮球把一个可拖动、可吸附屏幕边缘的悬浮球留在桌面上，用于快速打开
应用和内置工具。轮盘支持滚轮浏览与自下而上的扇形展开；AI 网页、笔记、
截图标注和键盘布局规则都从同一个入口启动。

## 下载

| 方式 | 文件 | 说明 |
|---|---|---|
| 便携版 | `YueYuOrb-1.0.0-win-x64-portable.zip` | 解压后运行 `YueYuOrb.exe`，无需安装 |
| 安装版 | `YueYuOrb-1.0.0-win-x64-setup.exe` | 当前用户安装，包含开始菜单入口和可选桌面快捷方式 |
| 校验值 | `SHA256SUMS.txt` | 用于核对上述两个发行文件 |

发行包为 .NET 10 self-contained win-x64，不要求目标机器安装 .NET SDK 或
.NET Desktop Runtime。

## 主要能力

- 悬浮球启动时回到主屏幕工作区，支持拖动、边缘吸附、位置锁定、时间显示、
  自定义图片和完整屏幕边界约束。
- 点击悬浮球后，内置功能与快捷应用在同一轮盘中自下而上扇形展开，支持
  平滑滚轮浏览。
- AI 网页使用独立 WebView2 窗口，可跟随应用的浅色/深色外观，并保留登录
  profile。
- 本地记事本使用 SQLite，支持自动保存、搜索、回收站和 TXT 导出。
- 截图支持框选、窗口、屏幕、倒计时、中文标注、剪贴板和自动另存。
- 键盘布局规则可将匹配应用的前台窗口锁定到已安装的英文非 IME 键盘，并在
  停止锁定、关闭规则或离开目标应用后恢复原布局。
- 包含托盘、热键、开机启动、单实例和跨会话数据锁。

## 安装与数据

安装器保持原有 AppId，安装目录为
`%LOCALAPPDATA%\Programs\YueYuOrb`。默认卸载只移除程序文件，保留
`%LOCALAPPDATA%\FloatDesk` 中的设置、笔记、悬浮球图片和 WebView2 profile；
只有在卸载器中主动勾选“清除本用户月语悬浮球数据”时才会删除该目录。

## 验证记录

- Release 构建：0 警告，0 错误。
- 自动化测试：190/190 通过（Core 74、Infrastructure 17、Windows 13、UI 86）。
- 自包含便携包：`startup.complete` 已观察，主实例持续存活，第二实例在
  5 秒内以退出码 0 完成单实例交接。
- 安装包：当前用户静默安装、开始菜单快捷方式、安装后启动、单实例交接和
  静默卸载均通过。
- 默认卸载保留 `%LOCALAPPDATA%\FloatDesk`，`settings.json` 在卸载前后哈希
  一致。
- SHA256 清单中的可执行文件、便携包和安装包均复核一致。

## SHA256

```text
661bb03b2637726ffb722874e23f3fea57519b1ea754e16da78585465c7335ca  publish/win-x64/YueYuOrb.exe
9b53c3689287bc94314447b528f63fb2ed56ece690cab1e9fb7fc48ee5dc370c  YueYuOrb-1.0.0-win-x64-portable.zip
b5102ff1ae0bd468a61a5331e8871410c46666796dfedc414af9b934438a349d  YueYuOrb-1.0.0-win-x64-setup.exe
```

## 兼容性与已知边界

当前自动化与发布验收宿主为 Windows 10 22H2 19045 x64。以下项目仍未完成
对应实机人工验收，因此不宣称通过：

- Windows 11 24H2/25H2、Windows 10 21H2 和标准用户干净虚拟机。
- 真实第三方输入法、自定义切换快捷键、提权目标窗口和中文输入豁免。
- DeepSeek 登录、提问、关闭重开和崩溃恢复。
- 混合 DPI、多屏旋转、HDR、独占全屏和游戏覆盖。
- 8 小时与 100 次操作资源检查。

安装包未进行代码签名。Windows SmartScreen 可能显示“未知发布者”警告；
请先核对 `SHA256SUMS.txt`，再决定是否运行。

## 开源许可

月语悬浮球使用 [GNU General Public License v3.0 only](../LICENSE) 开源。

作者：七月风语<br>
GitHub：[AthiefEX](https://github.com/AthiefEX)<br>
项目：[AthiefEX/YueYuOrb](https://github.com/AthiefEX/YueYuOrb)
