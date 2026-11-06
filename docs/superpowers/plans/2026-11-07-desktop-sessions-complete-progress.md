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
