# 月语悬浮球发布检查表

版本：1.0.0
声明：本版当前为“**待发布验收**”，不能把自动化测试 PASS 当成全兼容验收。

## 开发门禁

- [x] SDK 锁定为 .NET `10.0.400`。
- [x] NuGet 使用中央版本与 lock 文件。
- [x] Debug 构建 0 errors。
- [x] Release 自动化测试 189/189 通过
  （Core 74、Infrastructure 17、Windows 13、UI 85）。
- [x] 单实例交接冒烟通过。
- [x] `startup.complete` 可在存在热键冲突的环境中到达。
- [x] 设置并发字段合并、未来 schema 只读、笔记失败回退与删除保护、
  旧前台状态隔离、截图覆盖层取消、截图输出异常、前台身份复用、球图片图层有回归测试。
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

- [ ] Inno Setup 6 安装并生成
  `YueYuOrb-1.0.0-win-x64-setup.exe`。
- [ ] 标准用户安装、启动、升级与卸载。
- [ ] 默认卸载保留 `%LOCALAPPDATA%\FloatDesk`；仅用户勾选任务时清理。
- [ ] 干净 VM 无 .NET SDK 时启动自包含包。
- [ ] 干净 VM 无 WebView2 Runtime 时本地功能与修复引导可用。
- [ ] 安装包与 ZIP 均记录 SHA256。
- [ ] 未签名测试包在分发说明中明确标注；不得称为已签名正式发行版。
- [ ] 仓库根目录的 GPLv3 `LICENSE`、作者和 GitHub 链接可访问。

## 发布命令

```powershell
.\scripts\publish.ps1
```

脚本执行 locked restore、Release build、Release tests、self-contained publish、ZIP 和 SHA256。只有所有命令退出码为 0 才生成最终哈希文件。

## 已知环境缺口

- 当前构建机为 Windows 10 22H2 19045 x64。
- 当前无 Windows 11、Windows 10 21H2、无 WebView2 Runtime、混合 DPI/旋转屏和第二会话测试环境。
- 安装器生成前不得声称安装包已完成。
- 本机 `Ctrl+Alt+A` 被其他程序占用；月语悬浮球正确记录初始冲突并继续运行，托盘入口仍可用。
