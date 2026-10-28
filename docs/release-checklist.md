# 月语悬浮球发布检查表

版本：1.1.1
日期：2026-10-28

## 开发与打包门禁

- [x] SDK 锁定为 .NET `10.0.400`，NuGet 使用中央版本与 lock 文件。
- [x] 本次快捷应用功能已独立复审，无 Critical 或 Important 问题。
- [x] 产品常量、“关于”页、程序集默认版本、安装器与发布脚本统一为 `1.1.1`。
- [x] README 下载链接、用户手册与 `docs/release-notes-v1.1.1.md` 已更新。
- [x] 设置 schema 保持 `1`，旧设置默认显示快捷应用栏，原 `.lnk` 启动语义保留。
- [x] 发布构建使用 `artifacts/build` 隔离输出，避免覆盖正在运行的开发版 DLL。
- [x] 最终 Release 构建 0 警告、0 错误。
- [x] 最终 Release 自动化测试 666/666 通过（Core 144、Infrastructure 93、Windows 49、UI 380）。
- [x] self-contained `win-x64` 便携包与 Inno Setup 6.7.3 安装包生成。
- [x] 发布 EXE、便携包和安装包版本及 SHA256 复核。
- [x] ZIP 内 497 个文件与发布目录逐文件 SHA256 一致，包含 `LICENSE`、README 与 .NET 自包含运行文件。
- [x] 源码、`v1.1.1` 标签和三个发行附件同步到 GitHub；主分支与开发分支均已更新，公开 Latest API 已确认 `v1.1.1`，远端附件大小及 SHA256 与本地一致。

## 人工验证状态

- [ ] v1.1.1 独立主实例启动与单实例交接。本次构建时旧版进程仍在运行，未为发布而停止用户程序；v1.1.0 的既有冒烟结果不作为新版启动验收。
- [ ] 本版安装、升级和卸载交互。旧版本的安装验收记录见 `docs/manual-tests.md`。
- [ ] Windows 10 21H2/22H2 标准用户干净环境。
- [ ] Windows 11 完整 UI 流程。
- [ ] 实际第三方 EXE/快捷方式拖放、参数与工作目录启动。
- [ ] 浅色、深色、高对比度、100/125/150% 与混合 DPI 图标显示。
- [ ] 无 SDK 和无 WebView2 Runtime 的干净 VM。

上述未验证项在更新说明中明确披露，不把自动化测试通过等同于完整实机兼容验收。安装器最低版本保持 Windows 10 21H2（19044），目标平台为 x64。

## 发布命令

```powershell
.\scripts\publish.ps1 -Version 1.1.1 -Installer
```

脚本依次执行 locked restore、Release build、Release tests、self-contained publish、ZIP、安装器与 SHA256 生成；中间步骤失败即停止。构建中间产物和最终发布文件均位于仓库内的 `artifacts` 目录。

便携包无需安装，用户数据仍保存在 `%LOCALAPPDATA%\FloatDesk`。安装包未签名，发布说明保留 Windows SmartScreen 提示。GPLv3 `LICENSE` 与 README 随两种发行形式提供。

发布地址：[月语悬浮球 v1.1.1](https://github.com/AthiefEX/YueYuOrb/releases/tag/v1.1.1)。版本标签指向 `0907fd0`，包含快捷应用图标、拖放导入、应用栏开关与本次发布准备。发布后仅补充本检查表的完成状态。
