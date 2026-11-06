# 月语桌面分身双路线设计

日期：2026-11-06
状态：供实施与审查；本轮不编写产品代码。
基线：v1.4.0 / 5aee71b；实施前重新确认 git status。
实施者：用户手动切换 DeepSeek V4.1 Flash·APIGOTO。审查者：gpt-6-astra，reasoning_effort=low。

## 目标与路线

在不抢占主桌面焦点、鼠标和键盘的情况下运行应用与自动任务；月语统一管理本机 RDP 子会话及本地 Hyper-V Windows 虚拟机。两条路线独立交付、可同时运行，共用协议和任务模型。单个环境的人工输入和自动输入互斥。

本机路线使用微软公开 WTSEnableChildSessions、WTSIsChildSessionsEnabled、WTSGetChildSessionId 与 RDP ActiveX ConnectToChildSession。一个系统最多一个活动且连接的子会话；不得接管 DesktopClone 或其他应用已创建的会话。父会话结束会终止子会话。支持性以运行时检测和实机验收为准，不通过 Windows 版本字符串承诺一定可用。

虚拟机路线管理用户明确选定的 Hyper-V VM。首版连接现有、已安装 Windows 和助手的 VM，不自动下载系统镜像、购买授权或修改宿主机自启动。提供准备向导说明 Hyper-V、来宾系统、RDP 和助手要求；无法使用时显示具体原因。创建新 VM/安装系统是后续扩展，不冒充首版能力。

比较：本机路线轻量且可复用本机应用，但共享用户文件、注册表、全局单实例和硬件；虚拟机具有更强配置隔离，但需要磁盘、内存和来宾系统。普通本机 RDP 登录与隐藏 Desktop 不作为实现路线。

## 参考边界与已知证据

D:/DesktopClone 为编译发行物。只读 ILSpy 静态检查发现子会话检测、RDP ActiveX 连接、目标会话应用启动、重连注销和自启动抑制/恢复流程。没有执行该程序，也未验证其实际兼容性。参考程序会临时修改共享注册表、启动文件和计划任务；月语不照搬此策略。
不导入第三方反编译代码、资源、程序集或类结构。实现人员依据本文件与微软公开 API 文档独立实现；静态分析结论不等于严格人员隔离的 clean-room 认证。
资料：
- https://learn.microsoft.com/en-us/windows/win32/termserv/child-sessions
- https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpextendedsettings-property
- https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/enhanced-session-mode

## 架构与身份

主进程：设置、轮盘、任务编辑及运行状态。
SessionRuntime：持有每个环境的状态机、命令排队、超时、人工接管与运行记录。
ISessionBackend：本机与 Hyper-V 的能力检测、启动、展示、停止；不执行任务步骤。
YueYuOrb.SessionAgent：在目标用户交互会话执行 UI Automation、SendInput、进程启动和截图，绝不能在宿主会话代执行。
每个命令绑定 EnvironmentId(Guid)、Generation(Guid)、WindowsSessionId(uint)、CommandId(Guid)、DeadlineUtc；重连产生新 Generation，旧命令直接拒绝。
IDesktopSessionTransport：ConnectAsync返回并与SessionHandle核对Generation与LoginInstanceId；Acquire/Renew/ReleaseInputLease管理自动化输入租约；CancelAsync使用独立控制通道；SendAsync是唯一命令输入入口。SessionRuntime按环境串行派发，运行中最多一个命令，队列上限20，并为每条命令续约。
本机通过限定用户 SID 的命名管道，校验对端 PID、会话身份和一次性握手；不能仅信任客户端提交的 SessionId。虚拟机采用 Hyper-V socket 通道，限定 VM ID、服务 GUID并使用本次配对的秘密完成握手；秘密使用 DPAPI 保存，不写日志、命令行或普通设置 JSON。若 Hyper-V socket 不可用，报告不支持，不静默开放 TCP 监听。
本机协议Version=1使用4字节长度前缀和UTF-8 JSON，单帧上限1MiB。助手从标准输入的一次性引导通道读取32字节秘密，禁止通过命令行参数传递；宿主与助手各自生成16字节随机nonce，并以HMACSHA256双向证明环境、Generation、LoginInstanceId、会话和账户绑定。握手5秒超时，建立前同时核对管道对端PID、用户SID和Windows会话。已定义命令按CommandId去重，截止时间、Generation、会话不匹配均拒绝；取消走独立控制消息，输入租约到期或连接结束必须释放已按键。
助手默认标准用户运行。需提升的系统准备使用窄权限辅助进程；任务自身提权需用户显式允许，不把任意外部请求传给管理员 shell。

## 状态、窗口与生命周期

状态：Stopped、Starting、Ready、Running、Paused、Reconnecting、Stopping、Faulted。
Ready 要求后端连接、正确会话助手和输入能力均验证成功。掉线立即停止派发，进行中的非幂等步骤标为 UnknownOutcome，不自动重放。
展示窗口默认 1280x720 来宾分辨率，支持 1024x768、1920x1080 和自定义 800x600 至 3840x2160；不把宿主 DIP 当作来宾像素坐标。
关闭查看窗口选择“隐藏查看”或“停止环境”；隐藏不等同于 RDP Disconnect。只有实测可以维持渲染与输入时才允许后台 UI 任务继续，否则暂停并说明原因。
人工接管必须先等待在途输入结束，释放助手按下的键/按钮，再授予人工控制。恢复需要明确操作。
仅清理本次创建且身份仍匹配的进程与会话；预先运行的 VM 默认仅断开连接，不关闭电源。用户选择关机时优先来宾正常关机；强制断电单独确认。
父会话注销、睡眠、系统锁定、助手退出：暂停/取消工作，恢复后重做能力与身份校验，不假定前台和分辨率未变化。
不会承诺资源消耗为零，设置显示 VM 内存/CPU 配置但首版不主动修改现有 VM 资源。

## 自启动

每个环境有启动策略：EnvironmentDefault、ManagedOnly，并有独立应用列表。条目含 Id、显示名称、可执行文件或快捷方式、参数数组、工作目录、顺序、延迟、启用开关和是否等待退出。延迟 0–300 秒。
EnvironmentDefault 保留环境自身登录启动行为，另执行月语已启用列表。
ManagedOnly 要求后端明确报告 CanSuppressStartupBeforeLaunch；不支持时禁用并说明，不能静默把“启动后结束”描述为“从未启动”。
本机先做能力验证：探索官方会话启动机制是否能安全避免常规 shell 自启动；不临时改动共享 Run/StartupApproved、启动目录、计划任务或全局 shell。若不能可靠实现，首版本机 ManagedOnly 显示不支持，用户可选显式的“登录后清理指定应用”，仅对子会话的新进程按 PID、创建时间、可执行路径及会话匹配执行。禁止按进程名全局结束。
虚拟机的 ManagedOnly 仅对专用来宾账户配置已支持的启动来源，有变更记录与恢复功能；不能声称覆盖服务和第三方守护进程。扫描导入宿主自启动只复制清单，目标存在性和依赖须在来宾验证。
每次真正新建环境登录仅执行一次自启动清单；RDP 重连、窗口重新打开不重复启动。

## 自动任务和脚本接口

任务 JSON 是声明式有版本结构，不直接执行任意 JSON 表达式。首版步骤：
LaunchApplication、WaitForWindow、ClickElement、TypeText、SendKeys、Delay、CaptureScreenshot、RunScript。
控件定位优先 AutomationId/ControlType/Name；坐标点击是显式备选并绑定来宾分辨率。截图在来宾完成，不对宿主桌面误截。
任务触发：手动、EnvironmentReady、定时（本地日历时刻，记录时区；错过不补跑，环境未就绪则记跳过，不自动启动环境）。
每环境一个运行任务；最多排队 20 个请求；单任务最多 200 步。默认步骤超时 30 秒，可设 1–600 秒；等待/查询可重试 0–3 次，点击/输入/外部脚本默认不自动重试。
触发携带 RunId 去重；EnvironmentReady 按环境登录实例去重。
RunScript 显式选择可执行解释器和脚本路径/参数，不拼接 shell 命令；开始前确认配置，输出默认不持久化，启用日志时提示可能包含应用数据。取消结束该运行拥有的进程树，不影响其他会话。
宿主脚本使用本地受认证 IPC 客户端提交目标环境命令，不能将宿主 SendInput 自动重定向；已有脚本可搬入目标环境执行。
文本/密码输入、截图、配对信息不写常规日志；常规日志仅记录步骤类型、时间、状态、错误类别。

## UI 与项目兼容

设置新增“桌面分身”页：环境、自启动、任务、诊断四区；样式使用已有冰蓝玻璃、浅/深主题资源。轮盘增加“桌面分身”“运行任务”，与现有按钮相同尺寸、指示条和折扇动画。
功能默认关闭，不自动提权、不启用系统功能、不启动 VM；现有配置 SchemaVersion 保持 1，通过新增默认字段兼容。
Windows 10 22H2 x64 / Windows 11 x64，沿用 net10.0-windows10.0.19041.0，GPLv3。
VM、子会话视图不应用普通工具浮窗的失焦关闭逻辑，否则会打断自动化。
不调整其他已有功能。

## 验收与交付门槛

两条路线均验证：主机持续打字/移动鼠标时，来宾任务可操作；无宿主误输入；重连旧命令被拒；停止释放键鼠；人工接管互斥。
本机专项：已有外部子会话不接管；自启动策略不修改共享配置；单实例程序冲突给出说明；父会话结束/注销行为。
VM 专项：关窗不错误关机、外部既有 VM 不被销毁、认证失败拒绝命令、断连暂停不重放。
最小化、隐藏、断开、锁屏、DPI 变化必须分别测试并记录能力，不一概宣称支持。
安装/便携包包含助手；虚拟机内助手分发包和配置说明单独提供。未通过实机门槛的路线标为实验性，不影响另一条路线交付。
每任务实现 -> 测试证据 -> Astra low 审查 -> 修复 -> 再审；全功能最后审查。当前回合仅产出设计/计划，后续由用户切换指定实施模型。

## 审查修订：实施约束（2026-11-06）

以下条款细化并取代前文同主题的不明确描述，必须在共享契约冻结前纳入任务1。该版本不以“实验性”豁免身份和输入隔离失败。

### 1. 引导、权限及身份

本机启用与启动助手由安装准备阶段显式配置的窄权限broker完成。broker是独立服务，SYSTEM权限仅用于验证父/子会话关系、取得目标用户主令牌并在目标交互桌面启动固定路径的本产品助手（CreateProcessAsUser及正确environment block）。不得接受任意可执行路径、脚本或命令行；助手文件须位于不可被普通用户替换的受保护目录并核验安装清单哈希。便携版须先显式安装此组件，否则显示未准备。此能力需先做实机验证；失败则禁用本机后端，不退回宿主执行。
broker校验请求方用户SID、父会话及子会话关联；启动返回可信PID、创建时间、目标SID与登录认证LUID。初始化密钥通过仅broker及目标用户可读的一次性管道交付，不能放命令行。宿主与助手分别核对管道对端PID/创建时间、会话与产品文件身份，再做秘密挑战握手；nonce仅作新鲜度校验。
VM准备器在来宾安装系统broker及指定普通用户的会话助手。首次用户在来宾确认宿主配对，交换32字节随机配对秘密（不使用短PIN作为长期密钥），两端DPAPI保护。配对绑定VM GUID、来宾账户SID；来宾broker提供登录认证LUID和启动时间，拒绝Session 0、其他账户和旧登录。宿主程序不能仅凭网络返回的SID自证身份。
两条路线在Start部分成功失败时，按资源所有权记录回滚自己的助手/连接；不撤销他人会话、系统启动项或既有VM。

### 2. 输入租约与取消

自动任务运行时，嵌入RDP查看控件禁用键鼠转发（封装层过滤输入且校验ActiveX配置生效）；不使用阻断主机输入的BlockInput。助手控制租约有效期5秒，宿主每1秒续约；失效立即阻止新输入、释放已按键，并取消本运行拥有的脚本Job Object。
控制/取消通道独立于顺序命令队列；脚本不允许breakaway。若有无法确认终止的进程或输入，状态保持Faulted/Paused，不授予人工控制。
人工接管顺序：禁止新派发 -> 独立控制通道取消/排空 -> 助手释放确认 -> 开放查看端输入。恢复自动化先关闭查看输入再取得新租约。
外部RDP或VMConnect连接不在输入互斥保证范围；检测到会话切换或额外连接时暂停。专用VM/账户是运行条件，不能声称能阻挡同权限用户启动的所有其他控制工具。

### 3. VM查看与准备

首版使用嵌入RDP ActiveX的网络RDP连接来宾，不采用外部VMConnect窗口。用户配置VM GUID、来宾地址、专用账户；凭据用Windows凭据管理器存储，证书不匹配需明确处理，不自动忽略。
助手采用独立Hyper-V socket。准备向导以提升权限注册固定产品Service GUID的GuestCommunicationServices项，仅记录并卸载本产品新建项；已存在不匹配项时报冲突，不覆盖。来宾配置RDP与防火墙仅在用户确认准备动作后执行。
RDP登录事件后通过已配对broker核对专用账户当前唯一活动交互会话及登录实例；多会话歧义时不运行任务。RDP重连后必须重新核对。禁用剪贴板、磁盘、打印机、音频和设备重定向默认值；如用户启用则提示共享影响。
隐藏查看保持连接和渲染需专项实测，否则暂停UI步骤；关闭连接不等于关闭VM或授权助手在未知桌面继续工作。

### 4. 启动策略先于登录

增加Preparing状态和PrepareAsync(EnvironmentProfile,StartupPolicy,CancellationToken) -> PreparationResult，必须先于Start；结果含RequiresNewLogin、SupportedStartupSources及UnsupportedSources。
本机ManagedOnly默认不支持，只有安全实验证明能在登录前处理且不修改共享项才可启用，不作为首版承诺。
VM ManagedOnly限定专用账户的HKCU Run/RunOnce、该账户Startup文件夹；准备器在目标账户尚未登录时备份并禁用这些来源，记录前后值及哈希；检测到全机Run、公共Startup、服务、其他登录计划任务时明确不覆盖，不能显示“全部禁止”。UI名称说明“仅管理指定来源”；严格要求所有来源均不启动时报告不支持。
已有登录拒绝追溯应用，需用户注销后重新准备。恢复仅在当前值仍匹配本产品修改值时执行，有冲突不覆盖用户后续修改。恢复记录在来宾，卸载可恢复。助手服务不依赖上述被抑制启动来源。

### 5. 持久去重与登录身份

LoginInstanceId在任务1定义，来自受信broker的系统启动标识+账户SID+认证LUID+登录时间组合映射，不由每次宿主启动随机生成；Generation每次握手更新。
持久日志以(EnvironmentId,LoginInstanceId,StartupEntryId,EntryRevision)去重自启动；定时键为(TaskId,TaskRevision,TimeZoneId,计划本地日期时间)，夏令时重复时段只选一次，明确采用较早UTC时刻。
先原子持久化Started再执行，再记录Completed；崩溃后Started且无完成记录标UnknownOutcome，禁止自动补跑。自启动记录保留到确认登录结束后30天；定时记录保留90天，且不补跑已过期时刻。手动明确重新执行生成新RunId。
任何启动项或任务配置编辑必须通过`DesktopSessionConfigurationEditor`的生产更新边界，由其保留身份并生成新的EntryRevision或TaskRevision。禁止在内容变化后保留旧修订号，否则会错误复用执行去重记录。同一环境内启动项Id必须唯一，任务Id必须在全部环境中唯一，避免不同配置生成相同执行键。
宿主/助手重启时无法核实旧登录身份即暂停待重新建立，不虚构新登录触发自启动。

### 6. 后端事件与所有权

最终接口增加：
- PrepareAsync(EnvironmentProfile,StartupPolicy,CancellationToken): Task<PreparationResult>
- WatchAsync(Guid environmentId,CancellationToken): IAsyncEnumerable<SessionBackendEvent>
- StopAsync(SessionHandle,SessionStopMode,CancellationToken): Task
SessionStopMode为HideView、Disconnect、LogoffOwnedSession、ShutdownGuest；强制断电不在首版公开接口。
事件包含EnvironmentId、Generation、ObservedAt、Kind（Connected/Disconnected/Locked/Unlocked/ResolutionChanged/AgentExited/SessionEnded/CapabilitiesChanged）、登录实例和尺寸。
运行层只能通过`DesktopSessionStartCoordinator.StartAsync`启动后端；该生产门禁先调用`DesktopSessionPreparationGuard.RequireSuccessful`，再调用后端Start，不得仅依赖后端实现或测试替身检查准备结果。
Preparing -> Starting -> Ready需要准备、连接、认证均成功；掉线/锁屏/助手退出从Ready/Running转Paused或Reconnecting；代际校验成功且无UnknownOutcome才能恢复，运行中的任务不自动重放。SessionEnded转Stopped；错误清理不完整转Faulted。
资源账本记录VM GUID、会话认证LUID、PID/创建时间、拥有者和创建步骤。每次注销/终止/关机前重新查询匹配，无法证明所有权只断开自己的连接。外部修改及崩溃恢复不能扩大清理范围。
默认退出月语：取消自有任务，释放输入、断开自有查看；不关外部VM。显式注销仅允许当前拥有的子会话，重新启动时不把外部现存子会话当作可恢复资产。

### 7. 分路线交付门槛

共享协议、身份验证、所有权和输入闸门是硬门槛，任何失败禁用相关后端。兼容性尚未覆盖的OS/显示模式可标未验证，但不能以该标签开放已知隔离失败的能力。
任务9依赖共享任务通过及“拟启用路线”验收通过，不要求另一条未完成路线通过。发布包保留未完成路线入口但显示不可用；不能默认启用。
首轮风险验证的失败是结论，不要求修改系统限制强行通过。更新能力表后可继续另一条路线。

### 必增测试与任务归属

任务1：PreparationRequiredBeforeStart、ExplicitStopMode、LoginIdentityStableAcrossHostRestart。
任务2/3：HostCrashExpiresLease、ControlCancelBypassesBlockedCommand、UnkillableScriptBlocksTakeover、SessionZeroRejected、WrongAccountRejected、BootstrapSecretNotInArguments。
任务4：BrokerRejectsArbitraryExecutable、ChangedProcessIdentityNotTerminated、StartupBeforeSessionFailureRollsBackOwnedOnly。
任务5：GuestCommunicationRegistrationConflictPreserved、ViewAndAgentSessionMismatchPauses、ExternalConnectionPauses、RedirectsDisabledByDefault。
任务6：ExistingLoginRejectsManagedOnly、RestorePreservesConcurrentChanges、StartedWithoutCompletionNeverRelaunches。
任务8：PersistentScheduleKeySurvivesRestart、AmbiguousDstUsesEarlierUtcOnce。
任务9：UnsafeBackendDisabled、VerifiedBackendCanShipIndependently。
