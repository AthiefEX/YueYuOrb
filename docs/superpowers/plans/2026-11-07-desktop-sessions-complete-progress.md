# 完整分身实施进度与证据

2026-11-07：用户选择方案C，追加AI来宾系统管理、定时生命周期、MCP外部Agent任务生成。已完成设计与T01–T24实施计划；本次没有实施产品代码，也没有执行真实VM验收。

历史代码/发布不能作为本计划新能力的完成依据。尤其Hyper-V生产工厂仍拒绝启动；先前hyperv-fix发行缺少会话工具。保留此前所有未提交改动，实施从T01核验起。

## 任务状态

| 任务 | 实现 | 自动验证 | 真实验收 | 提交/证据 |
|---|---|---|---|---|
| T01–T24 | 未开始 | 未执行 | 未执行 | 见配套计划 |

## 外部前提（执行时一次检查）

- [ ] 宿主Get-VM、VMMS、权限、磁盘与虚拟化能力。
- [ ] 已有可测试Windows VM或可信Windows ISO；新建VM路径、系统版本和授权。
- [ ] 允许维护重启/测试VM关闭的范围；专用来宾账户准备。
- [ ] AI接口配置与凭据引用；可信软件安装来源及任务预授权范围。
- [ ] Win11及GPU正验收所需系统/硬件；不足则记外部阻塞，不编造通过。
- [ ] 可用的真实外部MCP客户端；无需内置AI密钥即可生成结构化任务。

## 每任务追加模板

任务/commit：
改动和生产入口：
修复前失败证据：
聚焦验证命令/退出码：
真实系统、VM GUID、步骤与结果（脱敏，无凭据）：
资源清理/失败恢复：
当前模型自审结论及修正：
剩余条件和下一任务：

## A01–A13验收

T01（发行工具与证据）：进行中。

已保全的执行前基线（2026-11-07）：
- HEAD：`d56e0af docs: record desktop session backend evidence`。
- 工作区包含既有桌面分身恢复、启动守卫和 Hyper-V 状态修复的未提交改动；本计划不重置、不覆盖这些改动。
- 当前 `scripts/publish.ps1` 已包含 App、SessionAgent、SessionClient、SessionBroker 的发布入口，但此前没有独立的产物完整性校验。

本轮 T01 改动：
- 新增 `scripts/verify-session-payload.ps1`，校验主程序、三类会话工具、文档、Agent SHA256 manifest，并拒绝缺文件或哈希不匹配。
- 发布脚本在压缩便携包前执行该校验；发布失败不会生成可误认为完整的便携包。
- Release 配置测试覆盖校验脚本中的关键文件约束。

验证：待运行聚焦测试、发布脚本验证和完整 Release 验证。
真实验收：待在干净发布目录启动真实设置窗口并检查完整工具目录；Hyper-V/来宾端到端不因打包通过而标记完成。

T02（宿主能力检测）：已完成本轮可执行部分。

- 修复前问题：管理工具状态依赖固定的 `Hyper-V.psd1` 路径；该路径缺失时会误报不可用，即使 PowerShell 能加载 `Get-VM`。
- 改动：使用系统原生 64 位 Windows PowerShell 的 `Get-Command Get-VM` 能力探测，并以 JSON 结构化结果读取；保留 DISM、BCD、systeminfo 的独立状态，不把“需重启”误报为已运行。
- 聚焦验证：`dotnet test tests/FloatDesk.Windows.Tests/FloatDesk.Windows.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter FullyQualifiedName~HyperVFeatureManagementTests`，23/23 通过；`git diff --check` 通过。
- 真实验收：当前会话没有执行 Hyper-V 安装或重启；尚未取得可用 VM，也未将 Hyper-V 标记为端到端可用。

T03（资源模型、配置迁移和预检）：已完成自动化契约部分。
- 新增 `VmResourceProfile`、宿主容量、资源校验、任务 DSL v2、来宾操作类型和生命周期计划类型。
- 资源校验包含 CPU、内存宿主保留、磁盘容量、绝对存储路径、网络交换机、GPU 模式和修订号；任务校验拒绝未授权路径与未知操作；凭据仅保存 `CredentialRef`。
- 聚焦验证：`DesktopAutomationContractTests`，3/3 通过；修复一个可空引用警告后再验证，Release 构建 0 警告 0 错误。
- 真实验收：尚未对真实 VM 修改资源；旧配置往返迁移和 UI 资源表单仍待后续任务。

T04（窄权限宿主 VM 管理代理）：已完成协议边界部分。
- 新增结构化 `HyperVManagementRequest`，只接受登记 VM GUID、固定操作枚举和同环境资源配置；资源变更脚本要求 VM 关机并拒绝任意 PowerShell 注入入口。
- 聚焦验证：`HyperVManagementProtocolTests`，2/2 通过；Release 构建通过。
- 真实验收：未在真实 Hyper-V VM 上执行变更；权限代理尚未接入服务端，不能标记生产 VM 管理完成。

T05（来宾交互 Agent 与管理操作）：已完成最小安全分发层。
- 新增 `GuestOperationDispatcher` 和来宾侧执行器，支持受控的启动/关闭应用与用户环境变量；相对路径、引号和换行路径会在执行前拒绝；不调用宿主执行入口。
- 聚焦验证：`GuestOperationDispatcherTests`，2/2 通过；Release 构建通过。
- 真实验收：尚未在隔离来宾桌面执行；服务安装、权限边界、UIAutomation/SendInput 及更多系统操作仍待 T06/T12。

当前连续执行位置：T06。后续生产 VM 端到端验收仍受“未提供现有 Windows VM/ISO 和来宾凭据”外部条件限制，不会用模拟协议测试替代真实验收。

T06（Hyper-V Socket 注册与生命周期）：已完成监听生命周期部分。
- 新增 `WindowsHyperVSocketListener`，固定 Service GUID，要求显式 Start 后才能 Accept，连接由独立 NetworkStream 管理并支持取消/清理。
- 聚焦验证：`HyperVSocketListenerTests`，2/2 通过；完整 Release 构建 0 警告 0 错误。
- 真实验收：当前主机没有可用已登录 Hyper-V 来宾，因此尚未执行真实 bind/listen、来宾 Agent 配对或重连验收；现有认证协议仍要求真实 BootId、用户 SID 和登录实例绑定。

T07（生产工厂和现有 VM）：已完成生产入口接线的安全降级部分。
- 移除 `HyperVProductionBackendUnavailable` 硬编码；AppRuntime 现在为 Hyper-V 组装 PowerShell 宿主、Socket 传输、DPAPI 配对存储和明确的未配对探针。
- 未配对时返回 `HyperVGuestAgentNotPaired`，不会把模拟身份或主桌面当作来宾；本机分身与 Hyper-V 后端保持分离。
- 聚焦验证：`HyperVProductionFactoryTests`，1/1 通过；T07 产物编译通过。
- 真实验收：尚未取得真实 VM/来宾 Agent，因此 Hyper-V 仍是“已接入生产入口、外部依赖未满足”，不标记端到端完成。

验证备注：完整串行套件目前为 Core 187/187、Infrastructure 93/93、Windows 181/181 通过；UI 706/707 通过，唯一失败是现有 `SettingsWindowLifecycleTests` 依赖的探针路径未找到（当前探针输出在 `tools/SettingsWindowProbe/bin/Release/net10.0-windows10.0.19041.0`，测试查找 `tests/.../bin/SettingsWindowProbe/release`）。这不是本轮产品断言失败，后续 T23 统一修复构建/探针产物路径后再复测。

T09（CPU/内存/磁盘/网络/GPU 设置）：完成受控资源管理边界。
- 新增 `IHyperVEnvironmentManager` 和 PowerShell 管理实现，资源读取使用结构化 JSON；资源应用要求环境匹配、VM 关机和固定 `Set-VM` 脚本，不开放任意脚本。
- 聚焦验证：`HyperVEnvironmentManagerTests`，1/1 通过；Release 构建 0 警告 0 错误。
- 真实验收：没有对真实 VM 执行资源变更，磁盘扩容、来宾分区扩展和 GPU 能力仍未验收。

T13（统一授权和任务验证）：已完成基础策略层。
- 新增 `TaskGrant` 和 `TaskPolicyEvaluator`，提交前校验任务修订、环境、操作集合、路径范围、过期时间及任务结束关闭权限；缺授权或扩大范围返回 `NeedsAuthorization`。
- 聚焦验证：`DesktopAutomationTaskGrantTests`，2/2 通过；Release 构建 0 警告 0 错误。
- 真实验收：尚未接入手动编辑器、AI 或 MCP 提交入口；这些入口必须继续复用该策略，不能各自绕过。

T14/T16（任务编辑基础与持久运行前置）：已完成任务草稿目录。
- 新增 `DesktopAutomationTaskCatalog`，支持任务草稿创建、读取和 expectedRevision 更新；采用临时文件替换，避免半写入。
- 修复任务 DSL 的接口集合无法由 `System.Text.Json` 反序列化问题，改用可序列化列表类型。
- 聚焦验证：`DesktopAutomationTaskCatalogTests`，1/1 通过；累计 Release 构建此前 0 警告 0 错误。
- 真实验收：尚未接入设置 UI、实际任务运行账本或来宾执行；这些仍需后续 T14-T18 完成。

T19（MCP stdio 入口）：已完成最小协议入口。
- 新增独立 `YueYuOrb.McpServer` 项目，stdio 输入输出，不监听网络、不读取凭据、不启动进程。
- 提供 `environments_list`、`tasks_schema`、`tasks_validate`、`tasks_create_draft` 四个工具；创建草稿明确不执行，后续 submit 必须接入统一 TaskGrant。
- 聚焦验证：`McpTaskSchemaTests` 1/1 通过；MCP Release 构建 0 警告 0 错误。
- 真实验收：尚未使用外部 MCP 客户端完成协议协商与任务链路；当前环境列表仍为最小占位，不能标记完整 MCP 交付。

下一未完成主线：T08/T10-T12、T14-T24。T08 的本机窗口已有历史实现但 Hyper-V 显示/配对未实机验收；T10-T12 仍需要真实 Windows ISO、来宾凭据和专用 VM，缺失时只完成可验证代码与阻塞诊断，不宣称端到端完成。
