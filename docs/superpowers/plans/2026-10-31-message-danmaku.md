# 消息弹幕 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为月语悬浮球提供经真实来源验证的 QQ、TIM、微信消息弹幕和完整显示配置。

**Architecture:** 轻量来源和可选本地桥接统一输出标准消息，经身份校验、筛选和有界调度驱动独立透明窗口。接入可行性先行，来源验证不通过时不进入依赖它的完整集成交付。

**Tech Stack:** .NET 10、WPF、Windows 通知与 UI Automation、本地认证 HTTP 桥接；现有设置仓库、主题和轮盘。

**Spec:** `docs/superpowers/specs/2026-10-31-message-danmaku-design.md`（用户已批准）。

## Global Constraints

- Windows 10 22H2 / Windows 11 x64；TFM `net10.0-windows10.0.19041.0`；SchemaVersion 保持 1。
- 默认关闭；不发送聊天消息、不切换会话、不收集登录密码、不持久化消息正文。
- 桥接仅 loopback、强随机令牌认证；不静默安装通知助手或第三方客户端插件。
- 图片与明确 @ 标记按真实能力降级；正文、群私聊区分和稳定会话身份是三平台完整接入的最低条件。
- 用户之前选择本会话直接实施的方式，执行时沿用；每任务提交精确文件，禁止 git add -A。
- 使用独立构建输出，避免覆盖正在运行的程序；不运行会清空 artifacts 的发布脚本。

## Review Focus

- 客户端升级导致会话身份缺失：失效关闭精准筛选，不将未知会话猜成私聊（Task 1、4）。
- 同一账号重连或换来源重复补发：只接收新事件，切换清队列（Task 3、4）。
- 超长文本和图片解码炸弹：限制输入与像素，失败不阻塞显示（Task 3、5）。
- 断屏及混合 DPI：区域保持可见，不抢焦点（Task 5）。
- 并发截图、锁屏、退出竞争：最后租约结束再恢复，已关闭来源不得回调重新显示（Task 7）。

## 文件边界与公共接口

新增目录 `src/FloatDesk.Core/Danmaku` 放消息、来源能力、筛选调度；`src/FloatDesk.Windows/Danmaku` 放轻量来源；`src/FloatDesk.App/Danmaku` 放桥接和运行时；显示窗口放现有 Views。

标准合同在 Task 2 固定：
- `DanmakuConversationKey(string Source, string AccountId, string ConversationId)`。
- `DanmakuMessage(string SourceEventId, DanmakuConversationKey Conversation, DanmakuConversationKind Kind, string SenderId, string SenderName, string ConversationName, string Text, DateTimeOffset ReceivedAt, bool IsLive, bool MentionsMe, IReadOnlyList<DanmakuImage> Images)`；Kind 包含 Unknown/Group/Private；图片为受限内存字节。
- `IDanmakuSource : IAsyncDisposable`：`SourceId`、`Capabilities`、`MessageReceived` 事件、`StartAsync(CancellationToken)`、`StopAsync(CancellationToken)`。状态事件不得携带正文或令牌。
- `DanmakuRuntime.ApplySettings(DanmakuSettings)`、`SetEnabled(bool)`、`SetPaused(bool)`、`Clear()`、`SuspendForCapture(): IDisposable`、`DisposeAsync()`。
- `DanmakuOverlayWindow.ApplySettings(DanmakuSettings)`、`Present(DanmakuPresentation)`、`Clear()`、`SetEditing(bool)`；presentation 含标准消息及已计算样式和优先级。

### Task 1：接入验证与停止条件

**Files:** 新建 `docs/message-danmaku-source-verification.md`；需要临时验证代码时仅放忽略目录 `work/message-danmaku-probe`。

- [ ] 读取现有发布结构，列出三客户端版本与可用来源。只检测进程/版本，不导出聊天内容。
- [ ] 验证通知授权与包身份要求，在便携和安装形式记录结果；若需签名助手，记录签名、安装、卸载需求及不可用状态，不静默安装。
- [ ] 分平台验证前台/后台/最小化/免打扰/关闭通知，记录正文、群私聊、稳定身份、图片、@、延迟、重复、历史补发。使用用户自发测试消息，不发送消息给他人。
- [ ] 对可选适配器评估维护状态、许可证、客户端版本和登录要求；如需安装或登录，先提供具体候选及所需操作再请求授权。禁止擅自选择注入方案。
- [ ] 验证至少一种来源满足每平台最低条件；任何平台不通过则提交报告，暂停依赖它的集成，请用户选择替代接入或范围。模拟消息不算通过。
- [ ] 只提交验证报告：`docs: record chat source feasibility`。

### Task 2：消息合同、配置与兼容

**Files:** 新建 `src/FloatDesk.Core/Danmaku/{DanmakuMessage,IDanmakuSource,DanmakuPresentation}.cs`、`src/FloatDesk.Core/Models/DanmakuSettings.cs`；修改 `src/FloatDesk.Core/Models/AppSettings.cs`、`src/FloatDesk.Infrastructure/Settings/JsonSettingsStore.cs`；测试 `tests/FloatDesk.Infrastructure.Tests/DanmakuSettingsTests.cs`。

- [ ] 写失败测试 `OldSettingsDisableDanmakuByDefault`：旧 JSON 加载得到 Enabled=false、图片=false、私聊立即=false；默认区域600×240、字号20、透明度0.9、3轨；SchemaVersion=1。
- [ ] 写 `InvalidSettingsAreNormalized`：NaN/Infinity坐标与透明度不能进入布局；字号8–96、轨道1–12、区域至少120×80；不存在字体回退系统字体。保留现有所有其他设置。
- [ ] 运行 `dotnet test tests/FloatDesk.Infrastructure.Tests -c Release --filter DanmakuSettingsTests -p:UseArtifactsOutput=true -p:ArtifactsPath=D:/FloatDesk/artifacts/build -p:NuGetAudit=false`，确认 RED。
- [ ] 实现公共合同和 `AppSettings.Danmaku { get; init; } = new()`；普通队列100/TTL30秒、提醒20/TTL60秒作为配置内固定限制。
- [ ] 同命令确认 GREEN；审查精确改动，提交 `feat: add danmaku contracts and settings`。

### Task 3：本地桥接与经验证的轻量来源

**Files:** 新建 `src/FloatDesk.App/Danmaku/{LocalDanmakuBridge,DanmakuBridgeCredentialStore}.cs`、`src/FloatDesk.Windows/Danmaku/{NotificationDanmakuSource,AutomationDanmakuSource}.cs`、`docs/message-danmaku-bridge.md`；测试 `tests/FloatDesk.Ui.Tests/DanmakuBridgeTests.cs`、`tests/FloatDesk.Windows.Tests/DanmakuSourceTests.cs`。若 Task 1 证明需要助手，先据报告补充助手的精确文件、签名/部署任务再实施，不把未打包主程序假设为可授权。

- [ ] 写失败测试：`RejectsMissingTokenAndNonLoopback`、`RejectsOversizedOrHistoricalEvent`、`RevokedPermissionStopsDelivery`；断言没有正文或凭据进入诊断。
- [ ] 桥接固定协议v1，POST `/v1/events`，Bearer令牌32随机字节，用户级DPAPI保存；请求上限3MiB、正文4096字符、最多4图，每图编码体积512KiB/解码4百万像素；每连接每秒20事件，超限429；JSON无类型反序列化。
- [ ] 按 Task 1 证据实现轻量来源，未知身份保持Unknown；只注册用户启用的进程，UIA不激活/切换聊天窗口。来源断开、撤权返回能力状态。
- [ ] 写 `SwitchingAccountRejectsLateCallbacks`，验证取消后旧回调不能发送事件；重连拒绝历史补发，退避1/2/4/8/16/30秒。
- [ ] 运行两个测试项目相应过滤器，记录 RED→GREEN；编写事件JSON和能力字段示例，精确提交 `feat: add authenticated local chat sources`。

### Task 4：过滤、去重与有界提醒调度

**Files:** 新建 `src/FloatDesk.Core/Danmaku/{DanmakuFilter,DanmakuScheduler}.cs`；测试 `tests/FloatDesk.Core.Tests/DanmakuSchedulerTests.cs`。

**Interfaces:** `DanmakuFilter.Evaluate(DanmakuMessage, DanmakuSettings): DanmakuPresentation?`；`DanmakuScheduler.Enqueue(DanmakuPresentation)`、`TakeNext(bool reminderLane, DateTimeOffset now)`、`Clear()`；注入TimeProvider。

- [ ] 写失败测试：群A通过/B拒绝；同名不同ID不混；Unknown不变成Private；排除私聊即使@也拒绝；立即私聊绕过普通队列；同事件只呈现一次。
- [ ] 写容量/TTL断言：第101普通消息淘汰最旧项，30秒后过期；21条提醒保持20条，60秒后过期；模拟时钟避免真实等待。
- [ ] 实现来源账号权威选择、稳定ID去重；缺ID辅助去重最多2秒并用发送者/会话/内容组合，来源切换清缓存；将关键词提醒与明确@分开。
- [ ] 运行 Core 项目过滤 Danmaku，确认 RED→GREEN；提交 `feat: schedule filtered chat danmaku`。

### Task 5：两种显示与区域编辑

**Files:** 新建 `src/FloatDesk.App/Views/DanmakuOverlayWindow.xaml`及`.xaml.cs`、`src/FloatDesk.App/Danmaku/{DanmakuLaneLayout,DanmakuImageDecoder}.cs`；测试 `tests/FloatDesk.Ui.Tests/DanmakuOverlayTests.cs`。

- [ ] 写失败测试：同轨不会追尾；纵向新消息底部进入；提醒槽替换且旧条目入有界提醒队列；极长文字不扩展窗口边界。
- [ ] 实现独立不激活穿透窗口，WPF位移/透明度动画，不逐帧布局；横向同轨同速，纵向正常横排；减少动画时静态更新。图片解码放隔离工作任务，限像素并冻结结果，超时500ms显示[图片]。
- [ ] 写并通过测试：畸形图片仍显示下一条、区域编辑结束恢复穿透、断屏返回主屏、DPI转换后可见。引用现有位置管理方式，不套用外部点击关闭行为。
- [ ] 用模拟内容渲染浅/深主题、不同字号和两模式截图检查；记录实际鼠标穿透和混合DPI仍需手测的项。
- [ ] 运行 UI 项目过滤 DanmakuOverlay，确认 RED→GREEN；提交 `feat: render configurable message danmaku`。

### Task 6：设置页、规则与预览

**Files:** 新建 `src/FloatDesk.App/ViewModels/SettingsViewModel.Danmaku.cs`；修改 `src/FloatDesk.App/ViewModels/SettingsViewModel.cs`、`src/FloatDesk.App/Views/SettingsWindow.xaml`及`.xaml.cs`；测试 `tests/FloatDesk.Ui.Tests/DanmakuSettingsViewModelTests.cs`。

- [ ] 写失败测试：群多选用稳定key；通知名匹配有显式能力提示；取消设置不污染已保存值；保存弹幕配置保留快捷应用/窗口位置/其他设置。
- [ ] 实现“消息弹幕”页，按设计提供来源、会话、外观、区域、图片、提醒全部选项；来源无能力时禁用对应配置并解释原因。缺字体回退，不静默改变保存的选择。
- [ ] 实现独立模拟预览及区域编辑按钮；预览事件不能进入真实来源或普通消息缓存。
- [ ] 运行 UI 过滤 DanmakuSettingsViewModel 和既有 SettingsViewModelMerge，确认通过；渲染选中页与滚动底部，检查导航胶囊无裁切。
- [ ] 精确提交 `feat: configure danmaku appearance and conversations`。

### Task 7：运行时、轮盘、快捷键及捕获

**Files:** 新建 `src/FloatDesk.App/Danmaku/DanmakuRuntime.cs`、`src/FloatDesk.App/AppRuntime.Danmaku.cs`、`src/FloatDesk.App/Views/QuickPanelWindow.Danmaku.cs`；修改 `src/FloatDesk.App/AppRuntime.cs`、`src/FloatDesk.Core/Models/AppSettings.cs`、`src/FloatDesk.Core/Models/LauncherModels.cs`、`src/FloatDesk.App/Views/QuickPanelWindow.xaml.cs`；参考 `src/FloatDesk.App/Views/QuickPanelWindow.Brightness.cs` 接入同一轮盘；测试 `tests/FloatDesk.Ui.Tests/DanmakuRuntimeTests.cs`、`tests/FloatDesk.Ui.Tests/DanmakuWheelTests.cs`。

- [ ] 写失败测试：轮盘开关显示一致；追加HotkeyAction枚举而不重排；没有默认快捷键冲突；暂停/清空不重播；停止释放所有来源。
- [ ] 实现公共Runtime接口并串联来源/过滤/调度/窗口，采用代次号拒绝旧回调；轮盘事件和设置变更统一经过Runtime。
- [ ] 集成字幕避让、夜间层次、截图/OCR隐藏；写 `TwoCaptureLeasesNeedTwoReleases`、`SessionLockDropsQueuedMessages`、`DisableRejectsLateImageAndSourceResults`。
- [ ] 运行 UI 相应过滤器及现有捕获/字幕/夜间用例确认 GREEN；精确提交 `feat: integrate message danmaku with orb controls`。

### Task 8：真实验收与最终检查

**Files:** 更新 `docs/message-danmaku-source-verification.md`；新建 `docs/message-danmaku-verification.md`，必要时补失败回归测试。

- [ ] 按设计验收矩阵运行真实三平台来源，明确版本、通知/桥接方式、未验证项；没有真实来源时停止完整交付声明。
- [ ] 测试刷屏、重连、撤权、账号切换、锁屏、图片失败、截图期间隐藏、多屏移除、窗口不抢焦点；消息内容和凭据不出现在日志。
- [ ] 运行 `dotnet test FloatDesk.sln -c Release -p:UseArtifactsOutput=true -p:ArtifactsPath=D:/FloatDesk/artifacts/build -p:NuGetAudit=false`；既有字幕超时用例如失败需与当前基线复现比对，不归因于新功能也不隐藏失败。
- [ ] 运行 `dotnet build FloatDesk.sln -c Release --no-restore -p:UseArtifactsOutput=true -p:ArtifactsPath=D:/FloatDesk/artifacts/build -p:NuGetAudit=false`；要求0警告0错误。检查git diff --check及任务文件范围。
- [ ] 独立审查整项改动，修复重要问题并针对性验证；提交验收文档。仅按用户后续授权发布GitHub版本，当前任务不推送或打标签。

## 自查结果

已映射设计中的接入、身份、来源状态、桥接安全、图片、两种显示、全部配置、群筛选/私聊提醒、生命周期与现有功能集成。阶段A是依赖关口；助手部署与具体第三方适配器只在有真实证据后细化，不虚构已确定实现。历史执行方式沿用本会话直接实施；本文确认后进入执行流程。
