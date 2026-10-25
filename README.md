# FloatDesk

FloatDesk 是面向 Windows 10/11 x64 的桌面悬浮助手。它以后台进程和托盘方式运行，提供悬浮球、二级快捷面板、英文输入规则、AI 网页小窗、本地记事本以及区域/窗口/屏幕截图与中文标注。

当前版本：`1.0.0`

> 本文档描述已经实现的产品与自动化验证结果。真实输入法、AI 登录问答、游戏全屏、混合 DPI 和 Windows 11 仍需按 [手工验收表](docs/manual-tests.md) 在目标机器验证，因此在完成这些项目前应标为“待发布验收”。

## 功能

- 悬浮球支持单击展开二级面板、拖动、边缘吸附、位置锁定、时间显示和自定义 PNG/JPEG/BMP 图片。
- 悬浮球支持普通置顶，以及全屏时“跟随置顶”“自动隐藏”“尽力保持置顶”三种策略。
- 快捷面板可启动 exe、快捷方式和文件夹，参数与工作目录可编辑，最多 48 项并支持滚动。
- 可手动或按 exe 路径自动请求英语（美国）非 IME 布局。界面只把目标线程实际读回的结果标记为“已验证”。
- AI 网页窗口按需创建 WebView2，支持自定义 HTTPS 站点、DeepSeek 默认站点、导航、缩放、固定和独立登录 profile。
- SQLite 记事本支持 500 ms 自动保存、正文搜索、回收站、恢复和 TXT 导出。
- 截图支持框选、当前窗口和当前屏幕，0/3/5 秒倒计时、矩形/箭头/中文文字标注、撤销重做、PNG/JPEG、剪贴板和自动另存。
- 当前用户单实例、跨会话数据锁、托盘、可配置热键、开机启动、配置备份与未来 schema 只读保护。

## 系统要求

- Windows 10 21H2/22H2 或 Windows 11，x64。
- Microsoft Edge WebView2 Runtime。缺失时仅 AI 网页不可用，悬浮球、笔记和截图继续工作。
- 发布包为 .NET 10 self-contained，不要求目标机器安装 .NET SDK 或 .NET Desktop Runtime。
- 首版以普通用户运行；提权应用、锁屏/UAC 安全桌面、受保护内容和独占全屏游戏不承诺控制或覆盖。

## 运行

开发构建：

```powershell
dotnet restore FloatDesk.sln --locked-mode
dotnet build FloatDesk.sln -c Debug --no-restore
dotnet run --project src/FloatDesk.App/FloatDesk.App.csproj
```

生成自包含发布包：

```powershell
.\scripts\publish.ps1
```

产物写入 `artifacts/`：

- `publish/win-x64/FloatDesk.exe`
- `FloatDesk-1.0.0-win-x64.zip`
- `SHA256SUMS.txt`

Inno Setup 定义位于 `installer/FloatDesk.iss`。当前构建机器没有安装 `iscc`，因此默认不伪造安装包；安装 Inno Setup 6 后可运行：

```powershell
iscc /DAppVersion=1.0.0 installer\FloatDesk.iss
```

## 数据与隐私

用户数据保存在 `%LOCALAPPDATA%\FloatDesk`：

- `settings.json` 与备份 `settings.json.bak`
- `data/notes.db` SQLite 笔记
- `assets/orb.png` 自定义悬浮球图片
- `WebView2/` AI 网页独立 profile
- `logs/` 启动与故障日志；不记录笔记正文、截图、按键、Cookie 或完整网页查询

设置页支持导入/导出偏好。导入不会执行任何快捷项。更高 schema 版本的设置会被识别为只读，当前版本拒绝覆盖。

## 快捷键

默认值如下；冲突时原绑定保持不变，托盘仍提供所有主要入口。

| 功能 | 默认快捷键 |
|---|---|
| 快捷面板 | `Ctrl+Alt+Space` |
| 框选截图 | `Ctrl+Alt+A` |
| 暂停/恢复英文规则 | `Ctrl+Alt+F12` |

可在“设置 > 常规”修改格式合法的快捷键。初始注册发生冲突时应用继续启动，并在日志写入 `hotkey.initial.failed`。

## 验证

自动化测试：

```powershell
.\scripts\test.ps1
```

进程冒烟：

```powershell
.\scripts\smoke.ps1
```

详细用法见 [用户手册](docs/user-guide.md)，验收状态见 [手工验收表](docs/manual-tests.md) 与 [发布检查表](docs/release-checklist.md)。

## 已知限制

- 只控制 Windows 键盘布局；不承诺第三方 IME 内部“中/英”状态。
- 应用必须在目标窗口实际接收 `WM_INPUTLANGCHANGEREQUEST` 后才显示已验证；提权应用和无英语布局会显示对应失败原因。
- 全屏覆盖、游戏反作弊、受保护内容、HDR 和多显示器混合 DPI 的实际表现依设备而异。
- WebView2 登录能力取决于目标网站；站点拒绝登录或网络不可用时可用默认浏览器回退。
- 当前未在 Windows 11、Windows 10 21H2、标准用户干净 VM、多屏混合 DPI 或无 WebView2 Runtime 机器上完成人工验收。
