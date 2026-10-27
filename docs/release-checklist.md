# 月语悬浮球发布检查表

版本：1.1.0
声明：本版发布物已完成当前构建机验收；不能把自动化测试 PASS 当成全兼容验收。

## 开发门禁

- [x] SDK 锁定为 .NET `10.0.400`。
- [x] NuGet 使用中央版本与 lock 文件。
- [x] Debug 构建 0 errors。
- [x] Release 自动化测试 625/625 通过
  （Core 138、Infrastructure 90、Windows 44、UI 353）。
- [x] 单实例交接冒烟通过。
- [x] `startup.complete` 可在存在热键冲突的环境中到达。
- [x] 设置并发字段合并、未来 schema 只读、笔记失败回退与删除保护、
  旧前台状态隔离、截图覆盖层取消、截图输出异常、前台身份复用、球图片图层有回归测试。
- [x] 翻译选中、框选 OCR、实时字幕、翻译服务、DPAPI 密钥、字幕放置和
  捕获排除有自动化回归测试。
- [x] 悬浮球图片手动裁切、裁切边界、时间无底板显示和替换图片即时刷新有
  自动化回归测试。
- [x] 扫描无 `NotImplementedException` 或生产占位按钮。
- [x] Release 构建/测试在最终代码上通过。
- [x] self-contained `win-x64` 发布包生成。
- [x] ZIP 与 SHA256 生成并复核。
- [x] 最终 Release 包在当前 Windows 10 22H2 宿主完成启动与单实例冒烟。

## 人工门禁

- [ ] Windows 10 21H2 标准用户完整启动。
- [ ] Windows 10 22H2 完整 UI 流程。
- [ ] Windows 11 24H2/25H2 完整 UI 流程。
- [ ] 真实英文布局请求、读回、拒绝对照、快速 Alt+Tab。
- [ ] 设置、AI、笔记和中文标注在手动锁定期间仍可中文输入。
- [ ] DeepSeek 登录、提问、关闭重开和崩溃重试。
- [ ] 框选、窗口、当前屏幕、跨屏、100/150/200% DPI、中文标注和粘贴。
- [ ] Edge F11、无边框游戏、独占全屏记录。
- [ ] Explorer 重启、睡眠唤醒、锁屏恢复。
- [ ] 8 小时与 100 次操作资源检查。

## 安装门禁

- [x] Inno Setup 6.7.3 生成
  `YueYuOrb-1.1.0-win-x64-setup.exe`，简体中文语言文件随仓库提供。
- [x] 当前 Windows 用户静默安装、开始菜单快捷方式、启动、单实例和卸载。
- [ ] 标准用户干净虚拟机安装、升级与卸载。
- [x] 默认卸载保留 `%LOCALAPPDATA%\FloatDesk`；`settings.json` 哈希前后一致。
- [ ] 干净 VM 无 .NET SDK 时启动自包含包。
- [ ] 干净 VM 无 WebView2 Runtime 时本地功能与修复引导可用。
- [x] 安装包、便携包和发布 EXE 均记录并复核 SHA256。
- [x] 未签名测试包在发布说明中明确标注 SmartScreen 风险。
- [x] 仓库根目录包含 GPLv3 `LICENSE`、作者和 GitHub 链接。
- [x] 便携 ZIP 与安装目录均包含 `LICENSE` 和 `README.md`。

## 发布命令

```powershell
.\scripts\publish.ps1 -Version 1.1.0 -Installer
```

脚本执行 locked restore、Release build、Release tests、self-contained publish、ZIP 和 SHA256。只有所有命令退出码为 0 才生成最终哈希文件。

## 已知环境缺口

- 当前构建机为 Windows 10 22H2 19045 x64。
- 当前无 Windows 11、Windows 10 21H2、无 WebView2 Runtime、混合 DPI/旋转屏和第二会话测试环境。
- 安装器生成前不得声称安装包已完成。
- 本机 `Ctrl+Alt+A` 被其他程序占用；月语悬浮球正确记录初始冲突并继续运行，托盘入口仍可用。
- 安装器验证宿主是管理员账户的当前用户安装模式，不等同于标准用户干净 VM。
- GDI 截屏测试需要交互桌面；Codex 独立沙箱桌面没有屏幕表面，发布门禁须在真实桌面会话运行。
