# Brightness and Night Mode Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** 所有可控屏幕同步硬件亮度，提供可穿透黑夜遮罩、轮盘入口及可配置快捷键。

**Architecture:** Core 定义配置、设备快照及多屏操作策略；Windows 后台服务封装 WMI/DDC；App 管理遮罩、面板和捕获暂停租约。复用现有音频轮盘和主题资源，遮罩开启状态仅驻留内存。

**Tech Stack:** .NET 10、WPF、Win32 显示配置及 Dxva2、WMI COM、xUnit。

**Spec:** `docs/superpowers/specs/2026-10-31-brightness-night-mode-design.md`

## Global Constraints

- Windows 10 22H2 x64、Windows 11 x64；SchemaVersion 保持 1。
- 保留旧设置及用户快捷键；HotkeyAction 仅尾部追加。
- 黑夜程度 0–90%，默认 40%；目标亮度 50%；步长 5 个百分点。
- 不停止用户正在运行的软件；使用独立构建产物目录；逐文件暂存。
- 不支持的屏幕明确反馈；不把请求值当作硬件成功读数。
- 当前任务不包含 GitHub 发布。

## Review Focus

- 同一显示设备的 WMI/DDC 重复发现不得导致双重写入。
- 驱动调用卡住不得阻止 UI、其他设备或紧急关闭遮罩。
- 拓扑变化或迟到回调不得重启已关闭遮罩或写入错误设备。
- 字幕期间启用黑夜/插入屏幕不得产生遮罩污染或周期闪烁。
- 混合 DPI、负坐标、贴边收缩不得使恢复入口不可操作。

## Task 1: 配置、接口与多屏策略

**Files:** 新增 Core `Models/BrightnessModels.cs`、`Contracts/IBrightnessService.cs`、`Brightness/BrightnessPolicy.cs`；修改 `Models/AppSettings.cs`；新增 Infrastructure `Settings/SettingsValidator.Brightness.cs` 并接入 `SettingsValidator.cs`；对应 Core/Infrastructure 测试。

**Interfaces:** `BrightnessSettings(int TargetPercent = 50, int StepPercent = 5, int NightDimmingPercent = 40, bool ShowBrightnessInWheel = true, bool ShowNightModeInWheel = true)`；`BrightnessDevice(string Id,string Name,int? Percent,string? UnavailableReason,bool IsPrimary)`；`BrightnessSnapshot(IReadOnlyList<BrightnessDevice> Devices)`；`BrightnessWriteResult(string DeviceId,int? ActualPercent,string? Error)`；`IBrightnessService.GetSnapshotAsync(CancellationToken)` 返回 Task<BrightnessSnapshot>，`SetPercentAsync(string deviceId,int percent,CancellationToken)` 返回 Task<BrightnessWriteResult>。服务实现 IDisposable。

- [ ] 写测试：旧 JSON 缺 Brightness 得到上述默认值，schema 为 1；程度 91、目标 101、步长 0 被拒绝；旧枚举数值不变。
- [ ] 运行新增测试观察 RED；实现配置/验证及 `BrightnessPolicy.CreateTargets(BrightnessSnapshot snapshot,int value,bool relative)` 返回 IReadOnlyDictionary<string,int>。
- [ ] 策略测试：20/80 相对 +5 得 25/85，98+5 得100；绝对50均为50；不可读/不可控屏跳过，零设备返回空。
- [ ] 运行 Core 和 Infrastructure Release 测试，期望全部 PASS；逐文件提交。

## Task 2: Windows 亮度后端

**Files:** 新增 `src/FloatDesk.Windows/Brightness/WindowsBrightnessService.cs`、`BrightnessDeviceDiscovery.cs`、`DdcBrightnessBackend.cs`、`WmiBrightnessBackend.cs`、`BrightnessInterop.cs`；新增 Windows.Tests `BrightnessServiceTests.cs` 和 `BrightnessNativeSmokeTests.cs`。

**Interfaces:** 实现 Task 1 IBrightnessService；内部 `IBrightnessBackend` 提供 `IReadOnlyList<BrightnessDevice> Enumerate()` 和 `BrightnessWriteResult SetPercent(string id,int percent)`；发现映射使用显示配置路径关联 WMI 实例与物理屏句柄，每个 endpoint 只指定一个后端。

- [ ] fake 后端测试先 RED：同屏去重、不可靠映射不写、设备范围归一化、读回失败不返回伪成功、句柄总被释放。
- [ ] 实现 WMI COM（不依赖已弃用的 wmic 命令）和 Dxva2 DDC 访问，保留后端错误为中文可用状态。
- [ ] 添加每设备串行后台执行、最新待写值覆盖、2秒逻辑超时及拓扑代次；超时设备仅允许一个未结束原生调用，不增生线程。
- [ ] 测试 A 阻塞时 B 完成、取消不触发排队写入、旧代次响应被丢弃、关闭不等待卡住调用；期望全部 PASS。
- [ ] 只读枚举真实硬件，不修改实际亮度；运行 Windows Release 测试并逐文件提交。

## Task 3: 黑夜控制与遮罩窗口

**Files:** 新增 `src/FloatDesk.App/Brightness/NightModeController.cs`、`NightModeState.cs`、`Views/NightDimWindow.cs`；新增 UI.Tests `NightModeStateTests.cs`、`NightDimWindowTests.cs`。

**Interfaces:** `NightModeController.SetEnabled(bool)`、`SetDimmingPercent(int)`、`IDisposable Suspend()`、`RefreshDisplays()`、`Dispose()`；`bool IsEnabled` 表示用户意图，`bool IsSuspended` 表示租约；所有窗口操作由 Dispatcher 执行。

- [ ] 状态测试先 RED：默认关闭、两次暂停一次释放仍隐藏、暂停期间关闭后最终释放不重启、租约重复释放无副作用、程度限制0–90。
- [ ] 实现每屏完整物理边界无激活/鼠标穿透遮罩，镜像区域去重；支持负坐标、DPI、插拔和锁屏隐藏恢复。
- [ ] 窗口测试验证样式、无任务栏、无焦点和指针穿透；拓扑事件及关闭回调取消订阅；运行新增 UI 测试期望 PASS。
- [ ] 逐文件提交。

## Task 4: 捕获、字幕与生命周期整合

**Files:** 修改 `src/FloatDesk.App/AppRuntime.cs`、`Coordination/WindowCoordinator.cs`、`Translation/TranslationRuntime.cs`；新增 `Brightness/NightModeCaptureCoordinator.cs`；新增 UI.Tests `NightModeCaptureTests.cs`。

**Interfaces:** `NightModeCaptureCoordinator.BeginCapture()` 返回 IDisposable 暂停租约；`SetSubtitleSessionActive(bool)` 建立/释放会话租约。初版统一将捕获排除视为未验证，走设计允许的暂停路径，不依赖 API 成功标志。

- [ ] 测试先 RED：截图/OCR 异常及取消释放暂停；字幕整个会话仅暂停一次；字幕中途开启黑夜/插入屏幕先暂停再显示；停止字幕后只按当前意图恢复。
- [ ] 接入实际截图、框选翻译、文字提取及字幕捕获入口；在暂停后的显示合成完成再采样；避免嵌套暂停恢复过早。
- [ ] 为 orb/轮盘/设置/亮度窗口维护遮罩之上的层级，不争抢前台；遵守全屏 orb 隐藏策略。退出先移除遮罩再清理硬件服务。
- [ ] 运行上述测试及原捕获/翻译回归期望 PASS；逐文件提交。

## Task 5: 面板、设置、轮盘、快捷键

**Files:** 新增 App `Views/BrightnessControlWindow.cs`、`Views/QuickPanelWindow.Brightness.cs`、`ViewModels/SettingsViewModel.Brightness.cs`；修改 `Views/SettingsWindow.xaml/.cs`、`ViewModels/SettingsViewModel.cs`、`Views/QuickPanelWindow.xaml.cs`、`AppRuntime.cs`、`Bootstrap/TrayService.cs`（先确认实际路径）；新增 UI.Tests `BrightnessControlWindowTests.cs`、`BrightnessSettingsTests.cs`、`BrightnessWheelTests.cs`。

**Interfaces:** 面板构造 `BrightnessControlWindow(IBrightnessService service,NightModeController nightMode,BrightnessSettings settings)`；轮盘事件 `BrightnessRequested`、`NightModeRequested`，状态方法 `SetNightModeEnabled(bool)`；快捷键尾部新增 BrightnessPanel、BrightnessPreset、BrightnessUp、BrightnessDown、ToggleNightMode、RecoverBrightness。

- [ ] 写 UI 测试先 RED：不同读数不触发初始写入、拖动统一设值、快速拖动保留末值、部分失败逐屏反馈、设置合并保留其他字段。
- [ ] 实现与音频面板相同主题，所有屏幕读数/状态及总滑条；100–150ms 合并写入，填充值保护防止递归写入。
- [ ] 添加设置页及快捷键编辑；紧急恢复默认建议组合键 Ctrl+Alt+Shift+F12，其余默认不绑定；冲突不静默替换用户设置。恢复动作立即关遮罩后独立请求所有设备50%。
- [ ] 轮盘按钮沿用42 DIP图标/胶囊及布局过滤，黑夜有勾选；面板显示阻止贴边收缩但不改变轮盘打开状态；托盘增加开关及恢复可见亮度。
- [ ] 测试轮盘布局/动画枚举包含新增按钮，快捷键冲突可见、相对增减按每屏读数，恢复先关闭遮罩；运行 UI Release 测试期望 PASS；逐文件提交。

## Task 6: 完整验证与交付

**Files:** 更新 spec 状态；本计划勾选完成项；新增本地验证记录于 `.superpowers/sdd/2026-10-31-brightness-night-mode/progress.md`。

- [ ] 执行 `dotnet test FloatDesk.sln -c Release -p:UseArtifactsOutput=true -p:ArtifactsPath=D:\FloatDesk\artifacts\build -p:NuGetAudit=false`，期望全部 PASS。
- [ ] 执行同参数 `dotnet build FloatDesk.sln`，期望0警告0错误；检查 git diff --check。
- [ ] 独立审查完整功能，修复影响正确性的发现并运行回归；记录不具备硬件验证条件的项目，不声称完成实测。
- [ ] 本地 self-contained win-x64 publish 至独立 `artifacts/brightness-night-mode/win-x64`；验证 exe 及依赖存在，提供用户测试路径。
- [ ] 手工验证可用时检查多屏、混合DPI、深浅色、截图/OCR、字幕暂停遮罩、播放视频及紧急恢复；硬件写入保留原值并恢复，未测项目明确列出。
