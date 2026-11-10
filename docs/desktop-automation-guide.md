# 自动任务、受信任 agent 与 MCP
适用：月语悬浮球 1.4.19 测试版，Windows 10 22H2 / Windows 11 x64。

## 一、最短使用流程

1. 退出旧版，解压完整便携包并运行 YueYuOrb.exe。
2. 打开「设置 → 自动任务」，在「规则、脚本、导入导出与 MCP」子标签复制 MCP 配置。
3. 将外部客户端原来的 command 路径更新为本包 desktop-sessions/mcp/YueYuOrb.McpServer.exe，重新连接。
4. 月语会弹出主题一致的连接确认窗。核对客户端名称及实际启动程序路径后选择「信任并允许操作」。
5. 如果没有及时确认或取消了弹窗，可直接在「自动任务 → 外部 agent 连接」开启该项开关，立即保存权限，不需要再点击应用。
6. 让 agent 先调用 environments_list，再调用 tasks_schema，使用返回的实际环境 ID 创建、更新或运行任务。
7. 新任务会即时显示在所选目标的列表里，无需关闭重开设置。已信任的 agent 不需要再逐任务或逐环境审核。

连接确认是非模态窗口，不会阻止你到设置里手动开启。首次调用会短暂等待确认；超时返回 ConnectionNotTrusted 后，
在月语中开启连接，再让 agent 重试同一次未保存操作。不要因不确定结果而盲目重复提交。

信任记录按客户端名称、真实启动程序路径及 SHA256 分开；名称由客户端自报，不是经过认证的品牌身份。
如果由 powershell.exe、cmd.exe、node.exe 等通用启动器启动，信任的是界面列出的启动程序，不是名称文字。
同一名称和同一启动程序会复用信任；程序文件更新后摘要变化，需要重新确认。关闭开关阻止后续外部操作，但不会停用已经保存的自动任务或回滚已完成操作。
任务及脚本使用当前 Windows 权限，来宾管理员操作仍需已保存的管理员凭据，不能绕过 UAC、安全桌面或未知密码。
此机制不构成对恶意同账号程序的安全沙箱。

## 二、三个任务目标

在独立的「自动任务」设置标签中选择：

| 标签 | 运行位置 | 前提 |
| --- | --- | --- |
| 本地分身 | 指定的独立 Windows 子会话 | 本地模式开启、服务与会话正常 |
| 虚拟机 | 指定 Hyper-V 来宾 | 虚拟机模式开启、来宾 Agent 配对、交互桌面就绪 |
| 全局任务 | 当前用户的主桌面 | 月语正在运行，交互桌面可用 |

本地/虚拟机标签再从下拉列表选具体分身，列表只显示该目标的任务。
全局目标固定为「主桌面（全局任务）」，不会创建、注销或借用一个子会话。
全局任务独立于分身总开关；关闭本地/虚拟机模式仍会阻止对应分身任务。

本地分身共用主机操作系统和账号资源。脚本仍可修改当前账号可访问的文件、注册表和系统配置，不能当作虚拟机隔离。
全局任务明确允许操作主桌面；运行鼠标、键盘或窗口动作会影响你正在使用的桌面。

## 三、开启任务后会发生什么

任务列表右侧开关修改后，手动设置需点击「应用」。外部受信任 agent 保存则立即生效。
首次保存为已启用、或由停用切换为启用时：

| 触发方式 | 行为 |
| --- | --- |
| Manual | 自动执行一次，不需要再点击“运行任务” |
| EnvironmentReady | 目标当前已就绪则去重触发，否则等待环境就绪 |
| Scheduled | 等待下一个 HH:mm 时点，不会无视计划时间立即执行 |

同一环境同一时刻仅运行一个任务；当前环境忙时会报告 TaskAlreadyRunning，软件不静默重试输入或安装操作。
修改一个始终保持启用的任务，不会因为保存任意文字而重新执行一次 Manual；可以 tasks_run 显式执行，或关闭再开启。
关闭任务不会中断已经开始的运行，请另外点击「停止最近运行」或使用 tasks_cancel。
定时任务要求月语运行和目标已就绪；错过时点不会在重连后补跑。全局 EnvironmentReady 表示月语主桌面执行宿主就绪。

升级不会自动启用旧任务。旧版已经强制改成 Manual 的草稿，软件无法猜回原本触发条件：
开启后会执行一次；若希望每次分身就绪时执行，请改成 EnvironmentReady 并保存，
或让受信任 agent 用 tasks_update 恢复原触发方式。新版本不再覆盖受信任调用的 enabled/trigger。

## 四、手动编写、导入、规则和脚本

所有子设置使用横向标签页。「手动添加」包含名称、触发方式、启用状态和步骤编辑；
「规则、脚本、导入导出与 MCP」包含规则文本、脚本、完整 JSON 和操作说明。

- 添加任务绑定当前所选目标；导入任务 JSON 自动分配新任务 ID，作为停用手动草稿，避免误执行下载的文件。
- 导出包含步骤、规则与内嵌脚本，移除旧 MCP 内容授权，不导出凭据库密码。
- 导入 .md/.txt 规则说明后点击保存规则。
- 导入 .ps1 只是载入编辑器；保存脚本并添加步骤才把正文与 SHA256 纳入任务。
- 简单脚本编辑器显示第一个脚本。多个脚本可在完整 JSON 中编辑；再次保存脚本会增加步骤，按需删除重复项。
- JSON 目标必须是当前所选分身；不能用其他分身已有的任务 ID 覆盖它。
- 刷新 JSON 会覆盖 JSON 输入区，请先保存编辑内容。外部更新会合并任务列表并保留正在编辑的内容；
  同一任务双方发生冲突时，需要重新加载、合并后保存。

规则是供人和 agent 理解目标的说明，**软件不自动把自然语言规则翻译成操作**。
要不连接 agent 也能复现，必须将成功的操作保存成确定步骤或脚本。
不要把密码、令牌放进规则、脚本、文本输入步骤或导出文件。

## 五、脚本执行方式

RunScript：以目标交互会话用户运行 .ps1 或已有 .exe。支持 scriptName 引用内嵌资产，或 scriptPath 引用目标机器上的绝对路径。
参数 arguments 是 JSON 字符串数组，例如 ["C:\\Users\\Public\\report.txt","hello"]。
脚本用 param([string]$path,[string]$message) 接收位置参数。

内嵌脚本运行时校验 UTF-8 正文 SHA256，并缓存到目标账号 LocalAppData/YueYuOrb/task-scripts。
实际 .ps1 缓存带 UTF-8 BOM，以兼容 Windows PowerShell 5.1 中文。摘要仍按正文 UTF-8、无 BOM 计算。
手动保存 JSON 会重算脚本摘要；MCP 与文件导入必须提供匹配摘要。

RunSystemScript：仅虚拟机可用，通过绑定的 VM ID、保存的管理员凭据和 PowerShell Direct 执行。
适合安装、卸载、机器环境变量、服务、注册表及系统配置；不适合启动交互窗口。
主桌面/本地分身不能冒充来宾管理员，此类型不会回退到主机。

普通脚本由受控 Job 管理，取消清理所属进程。管理员脚本的取消结束管理调用，
但已启动的安装器/服务可能继续，取消不等于回滚；未知结果先检查实际状态再运行。
请用绝对路径或环境变量，不依赖当前工作目录。脚本输出不作为聊天内容返回。

虚拟机首次需部署当前包的 Agent。已有来宾可在桌面分身高级设置「导出来宾初始化包」，
把整个目录复制进对应来宾，运行 Install.cmd，再测试 Agent 配对；不能只复制 EXE。
导出包含 VM 专用配对资料，应及时安装，成功后删除导出目录。
管理员凭据只在软件密码框验证保存，不要发给 agent 或写入任务。

## 六、JSON 字段与步骤

任务仍使用 DesktopSessionTask/1，AppSettings.SchemaVersion 保持 1。

| 字段 | 内容 |
| --- | --- |
| formatVersion | 固定 1 |
| id / environmentId | 非空任务 UUID / environments_list 返回的实际目标 UUID |
| name / revision / enabled | 名称、修订（从 1 开始）、启用状态 |
| trigger | kind=Manual/EnvironmentReady/Scheduled；定时填 localTime=HH:mm、Windows timeZoneId |
| steps | 最多 200 项，每项 id/kind/parameters/timeoutSeconds/retryCount |
| rules | 最多 32768 字符的说明 |
| scripts | 最多 8 个 .ps1：name/content/sha256；单项最多 65536 字符 |

不要手工伪造 Source、McpApprovalHash、McpEnvironmentBinding。受信任写入由月语设置相应来源和定义绑定。
parameters 的值全部是字符串，嵌套 JSON 也需要编码成字符串。

| kind | 参数 |
| --- | --- |
| LaunchApplication | startupEntry：启动项 JSON 字符串，含 id/displayName/executablePath/arguments/workingDirectory/order/delaySeconds/enabled/waitForExit/revision |
| CloseApplication | path：目标应用绝对路径；force=false 正常关闭，未保存内容可能需处理 |
| WaitForWindow | title 或 processName |
| ClickElement | automationId/name/className/controlType；或 strategy=coordinates、x/y、expectedWidth/expectedHeight |
| TypeText | text 及可定位控件字段 |
| SendKeys | keys，例如 Ctrl+S |
| Delay | milliseconds |
| ObserveDesktop | mode=windows/uiTree/screenshot；返回临时观察结果 |
| RunScript | scriptName 或 scriptPath，arguments 字符串数组 |
| RunSystemScript | scriptName，arguments；仅 VM 管理员 |
| SetBrightness | percent=0–100，仅全局目标，调整主机所有可控制屏幕 |

单步超时 1–600 秒。只有等待窗口支持受控重试，点击/输入/脚本不会自动重试。
坐标动作必须匹配分辨率。UIA 不保证覆盖游戏、绘图画布或更高权限窗口。
界面树最多 200 节点、4 层；截图为压缩预览，返回原始/预览尺寸。观察数据仅在运行状态中短暂保留。
传统 CaptureScreenshot 会写入明确的目标路径；临时观察请用 ObserveDesktop。

旧 V2 文件仅兼容 Fixed 模式、只含 path 的 StartApplication/StopApplication 草稿。
凭据引用、自动关机、复杂参数或其他操作无法无损映射，会拒绝并要求手动转换，不静默丢弃。

## 七、MCP 调用顺序

保持月语和客户端处于同一 Windows 用户、同一会话。
stdio 支持 2025-06-18 / 2025-11-25 初始化协商，无需开放 HTTP 端口。
标准客户端自动完成 initialize、notifications/initialized。

配置 command 使用本包 desktop-sessions/mcp/YueYuOrb.McpServer.exe 的绝对路径，args=[]。
必须更换旧 MCP 程序路径并重新连接；旧入口不认识新连接注册流程。

1. environments_list：区分 LocalChild、HyperV、Global，不猜目标 ID。
2. tasks_schema：取得格式和命令表。
3. tasks_validate：{"task":完整对象}，检查结构与目标类型。
4. tasks_create_draft：{"task":完整对象}。名称保留兼容，**已信任调用保留 enabled 和 trigger**，不是强制停用草稿。
5. tasks_get/tasks_list：确认保存内容、修订和目标。
6. tasks_run/tasks_submit：{"taskId":"UUID","environmentId":"UUID","runId":"新UUID"} 显式执行。
7. tasks_status：{"runId":"同一UUID"}，等待 Succeeded/Failed/Cancelled/UnknownOutcome。
8. tasks_cancel 请求停止，不代表回滚。

如果任务 enabled=true 且为 Manual，创建已经会触发一次，不要再无条件调用 tasks_run 造成两次操作。
更新：先 tasks_get，再 tasks_update，传 task 和 expectedRevision；修订冲突必须重新读取合并。
tasks_validate 不代表目标程序已安装、权限或硬件实际可用。

同一 runId 和定义只提交一次。记录最多 32 项且仅驻留内存；完成记录淘汰或软件重启后，不能把旧 runId 当永久防重凭据。
agent 应保存自己的操作状态，脚本应尽可能可重复执行。

desktop_step：传 environmentId、runId 和完整 step，按授权连接直接操作指定目标。
它与任务一样返回运行 ID，用 tasks_status 获取观察结果。
要离线复现，agent 必须将成功动作、等待条件和脚本保存成完整任务；不会自动录制所有临时鼠标动作。

## 八、示例与详细编写流程

docs/examples 提供 local-notepad.json、guest-user-script.json、guest-admin-script.json，
均默认为停用，界面导入绑定当前所选目标。通过 MCP 使用需替换真实 environmentId。
全局定时亮度示例仅有一步 SetBrightness/percent=35，
trigger 使用 Scheduled、localTime=22:00、timeZoneId=China Standard Time，enabled=true。
请先用 environments_list 找到 backend=Global 的目标；定时示例不会立即改变屏幕。

从自己的脚本生成 JSON：
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\docs\examples\build-task-example.ps1" -ScriptPath ".\demo.ps1" -EnvironmentId "真实UUID" -OutputPath ".\task.json"
虚拟机管理员脚本再加 -GuestAdministrator；生成器只写文件，不运行脚本。
先导入检查，再开启并应用。若不希望此刻运行，先保持停用或设置未来定时。

安装/卸载配方：
1. 在 VM 准备合法安装包，使用来宾绝对路径。
2. 脚本先判断已安装状态，核对安装包 SHA256，Start-Process -Wait -PassThru 后检查退出码与最终版本。
3. 静默参数按软件厂商确定。MSI 常用 /qn /norestart，不要盲目用于任意 EXE。
4. 卸载使用已核实的 MSI ProductCode 或厂商卸载程序，不做无差别删除。
5. 环境变量可用 [Environment]::SetEnvironmentVariable(name,value,'Machine')；新进程才继承更改。
6. 服务/注册表/网络操作先读取旧值，保存恢复步骤；不要通过关闭安全软件绕过失败。
7. 保存为 RunSystemScript，单步不超过 600 秒；长安装拆分执行与状态检查。
8. 先核对真实 VM 结果，再开启自动触发。任意安装器不一定支持无人值守。

## 九、错误处理与验收边界

ConnectionNotTrusted：到独立自动任务页开启正确连接，再重试；不要反复生成重复任务。
ConnectionNotFound：入口会尝试重新注册；仍失败时重连 MCP。
HostUnavailable：启动同用户同会话的月语并使用新完整包。
TaskRevisionConflict：重新读取、合并，不覆盖外部更新。
MainDesktopNotReady：主桌面已锁定、非交互桌面或不可访问；不会改用其他目标。
BackendModeDisabled/EnvironmentStartFailed：开启正确分身模式并检查实际环境/Agent。
GuestAdministratorCredentialRequired：在软件密码框保存来宾管理员凭据。
ScriptAssetInvalid/ScriptArgumentsInvalid：核对正文摘要和参数 JSON 字符串数组。
BrightnessUnavailable：没有可控制屏幕；BrightnessWriteFailed 可能部分屏幕已调整。
ApplicationNeedsAttention：处理未保存文档；force=true 可能丢失数据。

测试套件、模拟亮度服务、协议探针不能代替真实 VM 管理员安装卸载、游戏控制、Windows 11、混合 DPI 和锁屏验收。
主桌面控制具有实际影响，使用全局目标前请确认脚本。软件不保证所有应用和安装器都能自动化。
