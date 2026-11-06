# Desktop Sessions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement task-by-task.

**Goal:** 交付本机RDP子会话与Hyper-V虚拟机双路线的桌面分身、自启动与自动任务。
**Architecture:** 独立后端，共享状态机、受认证助手协议和任务执行器。完成共享契约后A/B并行，UI和打包集中整合。
**Tech Stack:** .NET 10、WPF、Win32 WTS、RDP ActiveX、Hyper-V socket、UI Automation、xUnit。
**Spec:** docs/superpowers/specs/2026-11-06-desktop-sessions-design.md

## 执行交接

必须先阅读设计末尾“审查修订：实施约束”。其7项修订是本计划所有任务的强制补充，覆盖旧版描述。任务1冻结PrepareAsync、WatchAsync、StopAsync的最终签名、LoginInstanceId与资源账本；任务2/3落实输入租约和broker引导；任务4/5分别落实子会话权限引导及网络RDP查看；任务6在登录前准备并持久去重；任务9按拟启用路线验收，未完成路线不可用。修订末尾的测试必须加入对应任务。

追加文件映射：任务1在Core/Models/DesktopSessionSettings.cs定义事件、准备结果、停止模式及身份；任务3新增src/FloatDesk.SessionBroker/（受保护安装的窄权限服务）与Windows/DesktopSessions/SessionBootstrap.cs，任务5新增Windows/DesktopSessions/HyperVPreparation.cs和App/DesktopSessions/GuestRdpViewHost.cs；任务6新增Infrastructure/DesktopSessions/SessionExecutionLedger.cs及其原子写入/崩溃恢复测试；任务9修改安装器支持显式安装/卸载broker和通信注册，便携版未准备时不得假装可用。所有新增项目加入解决方案及锁文件。

用户手动切换 DeepSeek V4.1 Flash·APIGOTO 后开始实施。当前工具无法调度该模型，不允许宣称已调用或默默换模型。
Astra 审查明确使用 model=gpt-6-astra、reasoning_effort=low。审查不代表实现者权限，也不保证系统兼容性。
本轮只完成文档与审查。每任务通过后记录提交与验证证据；不用每步询问用户。系统启用、VM启动/安装等有副作用操作仍按实际授权处理。

## Global Constraints

- Windows 10 22H2 x64 / Windows 11 x64；TFM net10.0-windows10.0.19041.0；SchemaVersion=1。
- 独立实现，不读取/复制参考程序反编译代码作为实现模板；仅用设计中的行为结论和官方API。
- 功能默认关闭；不得将输入回退至主桌面；不得修改共享宿主自启动配置。
- 每环境命令绑定身份、Generation和截止时间；重连禁止重放非幂等步骤。
- 一个系统仅一个活动且连接的子会话；外部创建的子会话不可接管。
- 主体、助手和调度器常规日志不含文本输入、密码、截图或配对秘密。
- 虚拟机首版使用已配置的既有VM；不自动创建新系统或承诺无资源影响。
- 计划路径为拟新增文件；每个(.cs)表示XAML及code-behind两文件。不得在大型现有文件中堆积完整实现。
- 初始检查git status及AGENTS；保留用户更改；精确文件暂存，不用git add -A，不推送main。

## Review Focus

1. 子会话编号复用、重连及冒名助手：任务2/3身份测试必须拒绝旧连接。
2. 共用用户配置和全局单实例应用：任务4/6不得修改主机或误杀主桌面。
3. 最小化、锁屏、隐藏后图形不可用：任务4/5实测后才开放后台能力。
4. 任务断线时点击已执行但应答丢失：任务7不得重复点击或输入。
5. 外部VM、夏令时及重新打开窗口：任务5/8不得关错VM或重复运行自启动/任务。

## 契约冻结与并行

任务1/2/3完成并审查后冻结共享协议。A1、B1在互不重叠文件中并行；只有选定实施模型可用于产品代码。
共享变更先由集成人员更新契约并通知另一支。任务6-8可先用假后端推进，但不能宣称后端已通过。
首轮先做本机ManagedOnly、隐藏RDP输入、VM助手认证三项风险验证；不可行时更新能力报告与设计，不暗中缩减含义。

## 通用验证与提交

每项顺序：写行为测试 -> 聚焦测试观察预期失败 -> 最小实现 -> 聚焦测试通过 -> Astra low审查 -> 修复/回归 -> 精确文件提交。
聚焦命令：dotnet test FloatDesk.sln -c Release --no-restore -p:UseArtifactsOutput=true -p:ArtifactsPath=D:/FloatDesk/artifacts/build -p:NuGetAudit=false --filter FullyQualifiedName~<测试类名>
最终命令：dotnet test FloatDesk.sln -c Release --no-restore -p:UseArtifactsOutput=true -p:ArtifactsPath=D:/FloatDesk/artifacts/build -p:NuGetAudit=false -- xunit.parallelizeTestCollections=false xunit.maxParallelThreads=1
构建命令：dotnet build FloatDesk.sln -c Release --no-restore -p:UseArtifactsOutput=true -p:ArtifactsPath=D:/FloatDesk/artifacts/build -p:NuGetAudit=false
如新增项目尚无restore资产，先运行locked restore；首次新增依赖需要生成并审查lock文件，不能把restore错误当作测试RED。
通过标准：所有用例通过，0警告0错误；UI自动化实机验证不由mock结果替代。
提交格式：git add -- <本任务明确文件列表>，git commit -m "feat: <本任务交付>"；提交前git diff --check。


### Task 1: 共享契约与配置

**Files:** src/FloatDesk.Core/Models/DesktopSessionSettings.cs; src/FloatDesk.Core/Contracts/IDesktopSessionBackend.cs; src/FloatDesk.Core/Models/AppSettings.cs; tests/FloatDesk.Core.Tests/DesktopSessionContractTests.cs

**Interfaces / implementation:** 定义 BackendKind(LocalChild,HyperV)、EnvironmentProfile、EnvironmentStatus、SessionHandle(EnvironmentId,Generation,WindowsSessionId,LoginInstanceId)、SessionCapabilities、StartupPolicy、StartupEntry、DesktopTask、TaskStep、TaskTrigger、SessionCommand、CommandResult。IDesktopSessionBackend: Task<SessionCapabilities> ProbeAsync(EnvironmentProfile,CancellationToken); Task<SessionHandle> StartAsync(EnvironmentProfile,CancellationToken); Task ShowAsync(SessionHandle,CancellationToken); Task StopAsync(SessionHandle,CancellationToken)。新增 AppSettings.DesktopSessions 默认空列表且 Disabled；JSON 保持 SchemaVersion=1。

**Dependencies:** 无

- [x] 写行为测试并断言：OldSettingsLoadWithSessionsDisabled；InvalidLimitsRejected：步骤201、队列21、超时0/601、自启动延迟301、重复环境ID被拒；RoundTripPreservesExistingPreferences。
- [x] 使用通用聚焦命令运行对应测试类，确认因目标行为缺失而失败。
- [x] 按本项接口实现，遵守设计中的数值上限、身份校验和生命周期。
- [x] 聚焦测试通过，提交Astra low审查diff及测试证据。
- [x] 修复发现并回归；记录已验证/未验证能力与提交哈希；精确暂存本项文件提交。

完成记录：`24bebfd..7dde2ea`；聚焦 Core 18/18、Infrastructure 18/18；完整 Release 测试 1071/1071，构建 0 警告/0 错误；Astra low 复审无 Critical/Important，确认可进入 Task 2。

### Task 2: 运行状态机与命令代理

**Files:** src/FloatDesk.App/DesktopSessions/DesktopSessionRuntime.cs; tests/FloatDesk.Ui.Tests/DesktopSessionRuntimeTests.cs

**Interfaces / implementation:** 定义 StartAsync(Guid,CancellationToken)、ExecuteAsync(SessionHandle,SessionCommand,CancellationToken)、TakeManualControlAsync(Guid,CancellationToken)、ResumeAsync(Guid,CancellationToken)、StopAsync(Guid,CancellationToken)。所有输入走助手 IDesktopSessionTransport.SendAsync(SessionHandle,SessionCommand,CancellationToken)，同时定义该 Core 接口及握手结果；按环境串行化并绑定 generation。

**Dependencies:** 任务1

- [x] 写行为测试并断言：StaleGenerationRejected；DisconnectMarksUnknownOutcomeWithoutReplay；ManualControlWaitsForInputRelease；QueueLimit20；StartCancellationCleansOnlyOwnedResources。
- [x] 使用通用聚焦命令运行对应测试类，确认因目标行为缺失而失败。
- [x] 按本项接口实现，遵守设计中的数值上限、身份校验和生命周期。
- [x] 聚焦测试通过，由当前模型完成验证审查（用户指定 Astra low 额度不足后改由当前模型审查）。
- [x] 修复发现并回归；记录已验证/未验证能力与提交哈希；精确暂存本项文件提交。

完成记录：聚焦 `DesktopSessionRuntimeTests` 11/11；完整 Release 测试 1082/1082，构建 0 警告/0 错误。已覆盖断线未知结果不重放、人工接管等待输入释放、独立控制取消、1 个运行+20 个排队上限、启动取消清理、握手代际不匹配拒绝、故障句柄未清理禁止重启，以及启动/停止清理失败后保留自有句柄供重试。尚未实机验证 RDP/VM 通道、真实助手输入释放和 5 秒租约到期；分别由 Task 3-5 的自动与实机门槛继续覆盖。

### Task 3: 会话助手与本机 IPC

**Files:** src/FloatDesk.SessionAgent/FloatDesk.SessionAgent.csproj; src/FloatDesk.SessionAgent/Program.cs; src/FloatDesk.SessionAgent/SessionCommandDispatcher.cs; src/FloatDesk.Windows/DesktopSessions/NamedPipeSessionTransport.cs; FloatDesk.sln; tests/FloatDesk.Windows.Tests/SessionProtocolTests.cs

**Interfaces / implementation:** 新建 net10.0-windows10.0.19041.0 x64 可执行助手，仅在正确交互会话运行。实现任务2 transport；定义 Version=1 握手、16字节加密随机nonce、最大1MiB消息、5秒握手超时；限制管道SID、校验对端PID与会话、generation/deadline/command去重。消息只接受已定义命令；拒绝主桌面目标。WindowsIdentity/SID核对不代替会话核对。

**Dependencies:** 任务1/2；助手基础先合并

- [x] 写行为测试并断言：WrongSessionCannotReceiveInput；SpoofedIdentityRejected；OversizedFrameRejected；ExpiredCommandRejected；DuplicateCommandNotExecutedTwice；CancelReleasesHeldKeys。
- [x] 使用通用聚焦命令运行对应测试类，确认因目标行为缺失而失败。
- [x] 按本项接口实现，遵守设计中的数值上限、身份校验和生命周期。
- [x] 聚焦测试通过，由当前模型完成验证审查（Astra low 额度不足）。
- [x] 修复发现并回归；记录已验证/未验证能力与提交哈希；精确暂存本项文件提交。

完成记录：`SessionProtocolTests` 13/13；完整 Release 测试 1095/1095，构建 0 警告/0 错误。已覆盖错误会话/账户、身份伪造、超大帧、过期命令、重复命令、取消幂等释放、执行器异常、启动秘密不进参数、16字节nonce及租约到期。命名管道传输、SessionAgent x64项目与安全ACL已编译。尚未实机端到端验证真实命名管道握手、真实助手进程会话发现和UI输入执行；实际自动化执行器按计划留到 Task 7，Task 4/5 接入后端时补实机验证。

### Task 4: A1 本机能力实测与 RDP 控件

**Files:** src/FloatDesk.Windows/DesktopSessions/ChildSessionNative.cs; src/FloatDesk.App/DesktopSessions/ChildSessionBackend.cs; src/FloatDesk.App/Views/DesktopSessionWindow.xaml(.cs); src/FloatDesk.App/DesktopSessions/RdpChildSessionHost.cs; tests/FloatDesk.Ui.Tests/ChildSessionBackendTests.cs; docs/manual-desktop-sessions.md

**Interfaces / implementation:** 以公开WTS API实现任务1 backend。WPF宿主中的WinForms AxHost/COM生命周期限定STA线程；扩展属性设置后连接。已存在子会话且无本产品所有权时拒绝。窄权限启用辅助路径与普通用户UI分离。先验证新建/重连/隐藏和主桌面交互，再接助手；不修改RDP系统文件或注册全局自启动项。能力报告含 CanRunHidden、CanSuppressStartupBeforeLaunch 和具体不可用原因。

**Dependencies:** 任务1/2/3；与B1并行

- [ ] 写行为测试并断言：ExistingForeignChildSessionNotTakenOver；ParentSessionNeverLoggedOff；FailedConnectDisposesCom；HiddenCapabilityMustBeMeasured；CancelStartDoesNotTerminateForeignSession。实机运行须明确同意启用子会话功能，自动测试使用假WTS后端。
- [ ] 使用通用聚焦命令运行对应测试类，确认因目标行为缺失而失败。
- [ ] 按本项接口实现，遵守设计中的数值上限、身份校验和生命周期。
- [ ] 聚焦测试通过，提交Astra low审查diff及测试证据。
- [ ] 修复发现并回归；记录已验证/未验证能力与提交哈希；精确暂存本项文件提交。

### Task 5: B1 Hyper-V 生命周期与助手通道

**Files:** src/FloatDesk.Windows/DesktopSessions/HyperVSessionBackend.cs; src/FloatDesk.Windows/DesktopSessions/HyperVSocketTransport.cs; src/FloatDesk.SessionAgent/HyperVAgentListener.cs; tests/FloatDesk.Windows.Tests/HyperVSessionBackendTests.cs

**Interfaces / implementation:** 选定VM GUID并只读检测Hyper-V/状态；启动既有VM，用户安装的来宾助手接受Hyper-V socket配对。实现任务1 backend及任务2 transport；配对协议Version=1，客户端/服务端随机挑战+HMACSHA256双向校验，重放防护与DPAPI保存；失败不开放TCP备用监听。RDP查看与助手任务通道分离。记录VM初始电源状态及是否由本次启动；停止默认断开，不强制断电。

**Dependencies:** 任务1/2/3；与A1并行，改动不同文件

- [ ] 写行为测试并断言：WrongVmRejected；BadPairingRejected；ReplayedHandshakeRejected；PreexistingVmNotPoweredOff；MissingHyperVExplainsUnavailable；ViewDisconnectDoesNotClaimTaskReady。
- [ ] 使用通用聚焦命令运行对应测试类，确认因目标行为缺失而失败。
- [ ] 按本项接口实现，遵守设计中的数值上限、身份校验和生命周期。
- [ ] 聚焦测试通过，提交Astra low审查diff及测试证据。
- [ ] 修复发现并回归；记录已验证/未验证能力与提交哈希；精确暂存本项文件提交。

### Task 6: 自启动清单与能力降级

**Files:** src/FloatDesk.App/DesktopSessions/SessionStartupCoordinator.cs; src/FloatDesk.SessionAgent/StartupApplicationRunner.cs; tests/FloatDesk.Ui.Tests/SessionStartupTests.cs

**Interfaces / implementation:** 定义 RunOnceAsync(SessionHandle,IReadOnlyList<StartupEntry>,CancellationToken)。以登录实例标识去重，重连更新generation但保留登录实例ID（在任务1 SessionHandle增加LoginInstanceId，由后端新登录生成、重连复用）。先检查策略能力，再按序执行参数数组；快捷方式在目标环境Shell解析。ManagedOnly本机做安全能力实验，若不支持禁用；不以杀进程伪装禁止启动。来宾专用账户的支持来源和恢复范围记录在能力报告。

**Dependencies:** A1/B1能力契约；可先用假后端

- [ ] 写行为测试并断言：ReconnectDoesNotRelaunch；NewLoginRunsOnce；UnsupportedManagedOnlyRejected；MissingGuestPathFailsClearly；NoSharedStartupMutation；CleanupNeverTargetsParentSession。
- [ ] 使用通用聚焦命令运行对应测试类，确认因目标行为缺失而失败。
- [ ] 按本项接口实现，遵守设计中的数值上限、身份校验和生命周期。
- [ ] 聚焦测试通过，提交Astra low审查diff及测试证据。
- [ ] 修复发现并回归；记录已验证/未验证能力与提交哈希；精确暂存本项文件提交。

### Task 7: 任务执行器与宿主脚本客户端

**Files:** src/FloatDesk.App/DesktopSessions/DesktopTaskRunner.cs; src/FloatDesk.SessionAgent/AutomationStepExecutor.cs; src/FloatDesk.SessionClient/Program.cs; tests/FloatDesk.Ui.Tests/DesktopTaskRunnerTests.cs

**Interfaces / implementation:** 实现 Task<RunResult> RunAsync(SessionHandle,DesktopTask,CancellationToken)，RunResult含Succeeded/Failed/Cancelled/UnknownOutcome。实现八种步骤及控件定位；坐标模式校验来宾分辨率。脚本客户端提供 list、run --environment ID --task ID、cancel --run ID，使用受认证本地IPC；任务定义由设置侧保存，不接收外部任意提权命令。脚本输出默认不持久化，暂停/取消不终止非本次进程。

**Dependencies:** 任务2/3；接后端后双路线验收

- [ ] 写行为测试并断言：ClickNotRetriedAfterLostAck；WaitRetriesAtMost3；CancelKillsOnlyOwnedScriptTree；CoordinatesRejectChangedResolution；PasswordsNotLogged；HostInputApiNotCalled；200StepsAccepted201Rejected。
- [ ] 使用通用聚焦命令运行对应测试类，确认因目标行为缺失而失败。
- [ ] 按本项接口实现，遵守设计中的数值上限、身份校验和生命周期。
- [ ] 聚焦测试通过，提交Astra low审查diff及测试证据。
- [ ] 修复发现并回归；记录已验证/未验证能力与提交哈希；精确暂存本项文件提交。

### Task 8: 触发、编辑与轮盘整合

**Files:** src/FloatDesk.App/DesktopSessions/DesktopTaskScheduler.cs; src/FloatDesk.App/ViewModels/SettingsViewModel.DesktopSessions.cs; src/FloatDesk.App/Views/SettingsWindow.DesktopSessions.cs; src/FloatDesk.App/Views/SettingsWindow.xaml; src/FloatDesk.App/AppRuntime.DesktopSessions.cs; src/FloatDesk.App/AppRuntime.cs; src/FloatDesk.Core/Models/LauncherModels.cs; src/FloatDesk.Core/Models/AppSettings.cs; src/FloatDesk.App/Views/QuickPanelWindow.xaml(.cs); tests/FloatDesk.Ui.Tests/DesktopSessionSettingsTests.cs

**Interfaces / implementation:** 定时按记录时区计算，夏令时不存在时间跳过、重复时间只执行一次；每次触发RunId去重，错过不补跑。新增设置页与八种步骤表单编辑、上下移动、验证、试运行、停止、日志。轮盘DesktopSessions/RunDesktopTask两个动作，追加枚举不改变旧值；沿用现有事件/图标/折扇运动路径。设置保存遵循现有合并机制，默认关闭。会话窗口不自动失焦关闭。

**Dependencies:** 任务1/2/6/7；共享文件由单一集成人员修改

- [ ] 写行为测试并断言：ScheduleMissedWhileUnavailableIsSkipped；DstDuplicateTimeRunsOnce；ReconnectDoesNotTriggerReadyTaskAgain；OldWheelItemsRemainValid；SettingsMergePreservesOtherPages；SessionViewNotDismissedOnDeactivate。
- [ ] 使用通用聚焦命令运行对应测试类，确认因目标行为缺失而失败。
- [ ] 按本项接口实现，遵守设计中的数值上限、身份校验和生命周期。
- [ ] 聚焦测试通过，提交Astra low审查diff及测试证据。
- [ ] 修复发现并回归；记录已验证/未验证能力与提交哈希；精确暂存本项文件提交。

### Task 9: 打包、诊断与完整验收

**Files:** src/FloatDesk.App/FloatDesk.App.csproj; scripts/publish.ps1; installer/FloatDesk.iss; docs/user-guide.md; docs/manual-desktop-sessions.md; tests/FloatDesk.Ui.Tests/ReleaseConfigurationTests.cs

**Interfaces / implementation:** 将助手与客户端完整依赖纳入便携包和安装包；生成独立来宾助手包、首次配对向导和诊断。运行原有测试并新增集成冒烟；分别记录Windows10/11、最小化/隐藏/断线/锁屏、混合DPI、主桌面持续输入、外部子会话冲突。未实测项标未验证，不能写通过。默认不启动任何环境。最终Astra low检查无Critical/Important后交付本地测试包；GitHub发布另待用户授权。

**Dependencies:** 所有任务及两条路线审查通过

- [ ] 写行为测试并断言：PackageContainsAgentDependencies；DisabledFeatureDoesNotProbeOrLaunch；UpgradeKeepsSettings；FullSuiteRegression；DualBackendIsolationManual。
- [ ] 使用通用聚焦命令运行对应测试类，确认因目标行为缺失而失败。
- [ ] 按本项接口实现，遵守设计中的数值上限、身份校验和生命周期。
- [ ] 聚焦测试通过，提交Astra low审查diff及测试证据。
- [ ] 修复发现并回归；记录已验证/未验证能力与提交哈希；精确暂存本项文件提交。

## 审查提交模板

提交任务编号、实际变更文件、起止commit或未提交diff、运行命令/退出码、实机操作及结果。Astra仅检查当前范围，按Critical/Important/Minor给出文件定位与复现；Critical/Important未清除不得进入依赖任务。连接权限问题不伪装为功能实现失败。

## 自检

## 审查记录与交接状态

- 2026-11-06：以model=gpt-6-astra、reasoning_effort=low调用独立审查，agent_id=01a1aefe-720e-7432-a8fe-33d5c466373c。
- 首轮7项Important已通过设计末尾修订及计划任务映射处理。
- 复审结论：可以交接设计与实施计划；无剩余阻断交接的Critical/Important。仅文档通过，不代表实机能力已验证。
- 当前仅新增设计与计划；产品代码、系统设置、VM均未改动。由用户手动切换DeepSeek V4.1 Flash·APIGOTO后，从Task 1开始执行。
设计目标映射：双后端4/5；身份及键鼠隔离2/3/7；自启动6；任务/脚本7；触发/UI/轮盘8；发行/兼容9。所有共享接口在1/2定义，登录实例ID应在任务1即定义（任务6明确用途）。任务数量9，A/B共享同一验收基线。
