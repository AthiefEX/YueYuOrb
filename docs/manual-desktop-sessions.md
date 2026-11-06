# 桌面分身本机子会话验证

本文件记录 Windows 10 22H2 x64 与 Windows 11 x64 上需要人工确认的能力。自动测试不会启用系统子会话，也不会创建 RDP 连接。

## 发布包布局

主安装包和便携包包含以下工具：

```text
YueYuOrb.exe
desktop-sessions/
  agent/YueYuOrb.SessionAgent.exe
  client/YueYuOrb.SessionClient.exe
docs/
  manual-desktop-sessions.md
  user-guide.md
```

发布脚本另外生成 `YueYuOrb-<版本>-session-agent-win-x64.zip`，用于复制到来宾虚拟机。主包的“桌面分身”诊断区会检查助手、客户端和 broker 是否齐全，但不会探测 Hyper-V 或启动任何会话。

当前仓库尚未提供 `desktop-sessions/broker/YueYuOrb.SessionBroker.exe` 或生产 `IDesktopSessionExecutionHost` 接线。因此当前构建必须保持以下默认结果：

- 诊断显示“会话运行时未准备”和“窄权限准备组件未提供”。
- 环境启动、任务运行与控制管道均明确拒绝。
- 不创建子会话、不启动虚拟机，也不把输入回退到主桌面。

只有加入并通过安全验收的窄权限 broker、固定路径助手引导和哈希清单后，才可以安装服务并开启本机路线。安装器不得在没有 broker 时伪装注册成功；卸载只删除本产品创建的注册项，不修改预先存在的系统配置。

## 前置条件

- 仅在已获得用户明确同意后启用 Windows 子会话功能。
- 使用 `WTSIsChildSessionsEnabled` 检查状态；需要启用时由窄权限准备流程调用 `WTSEnableChildSessions`。
- 月语必须位于当前用户已有交互会话中。父会话注销会终止子会话。
- 一个系统同时只允许一个活动且已连接的子会话。若检测到非月语创建的子会话，必须拒绝启动，不接管、不注销。

## 人工验收

1. 在父桌面持续移动鼠标并输入文字，启动月语本机子会话。
2. 确认 RDP 视图连接成功，`WTSGetChildSessionId` 返回的会话号既不是 `0`/`uint.MaxValue`，也不是父会话号。
3. 在子会话启动记事本并输入文字，确认父桌面鼠标、键盘和前台窗口不被抢占。
4. 隐藏查看窗口，确认只是隐藏，不调用注销；重新显示后会话仍存在。
5. 断开查看窗口，确认只结束月语自己的连接，不注销父会话。
6. 取消启动、连接失败或 RDP COM 初始化失败时，确认已创建的 ActiveX 控件被释放，且没有调用 `WTSLogoffSession`。
7. 若已有外部子会话，确认启动被拒绝并给出明确原因。
8. 分别检查 100%、125%、150%、200% DPI 以及 Windows 10/11；记录实际结果，不根据系统版本推断支持。

## 当前自动测试边界

- WTS 使用假后端验证所有权和父会话保护。
- RDP `IRdpControl` 使用假控件验证“配置后连接”和失败释放。
- 真实 `MsRdpClient9NotSafeForScripting`、命名管道助手、混合 DPI 与 Windows 11 仍需人工验收。

发布验收还必须确认安装包和便携包均含 `desktop-sessions/agent`、`desktop-sessions/client` 与文档；禁用功能启动时没有 Hyper-V/WTS 探测进程，且控制客户端返回不可用而不是主桌面执行。

## Hyper-V 路线

### 前置条件

- 宿主机已启用 Hyper-V，且 `Get-VM` 可正常管理目标虚拟机。
- 只选择已安装 Windows、RDP 和月语来宾助手的现有虚拟机；首版不创建或安装 VM。
- 来宾助手的 Hyper-V socket 服务 GUID 必须与宿主机配置一致，配对秘密固定为 32 字节并由当前用户 DPAPI 保护。
- 不开放 TCP 备用监听；Hyper-V socket 不可用时必须显示不可用原因。

### 人工验收

1. 记录 VM 初始电源状态；选择已运行 VM 时，确认月语不会重复启动或关机。
2. 选择关机 VM，确认只启动该既有 VM，并在超时内进入 `Running`。
3. 使用配对秘密完成双向 HMAC 证明；错误秘密、错误 VM、错误服务 GUID 和重放握手必须被拒绝。
4. 确认 RDP 查看连接与助手任务通道为两个独立连接；断开查看窗口不得把助手通道标记为 Ready，也不得关闭 VM。
5. 隐藏查看窗口、普通断开和显式关闭来宾必须分别记录；普通断开不强制断电。
6. 检查 100%、125%、150%、200% DPI，Windows 10 22H2 x64 与 Windows 11 x64。

### 当前自动测试边界

- Hyper-V 宿主使用假后端验证只启动既有 VM、父 VM 不因普通断开而关机，以及不可用原因。
- 配对协议验证错误秘密和重放拒绝；内存双向流验证宿主与来宾监听器握手。
- 真实 Hyper-V socket、`Get-VM`/`Start-VM`、DPAPI 文件、来宾助手、RDP ActiveX 和混合 DPI 仍需人工验收。

## 会话自启动

### 执行规则

- 以 `EnvironmentId + LoginInstanceId + StartupEntryId + Revision` 去重；RDP 重连只改变 Generation，不重复启动。
- 仅允许配置的绝对路径 `.exe` 与 `.lnk`；脚本、相对路径和覆盖快捷方式参数的配置会被拒绝。
- `.exe` 使用参数数组直接启动，不经过命令字符串；`.lnk` 由来宾会话 Shell 解析，并保留快捷方式自身的参数与工作目录。
- 普通启动失败会返回明确错误码；取消等待中的自有进程时会终止该进程树，不注销会话。
- 执行账本位于应用数据目录，使用原子替换与备份快照；不会写入宿主机启动文件夹、Run 注册表项或计划任务。

### 人工验收

1. 同一登录实例断开并重连，确认 Generation 改变但自启动列表不重复执行。
2. 注销后重新登录，确认使用新的 LoginInstanceId 再执行一次。
3. 修改条目并提升 Revision，确认新修订被视为一次新的执行键。
4. 检查缺少路径、相对路径、脚本和带参数覆盖的 `.lnk` 均显示明确错误且未启动进程。
5. 检查主桌面启动文件夹、Run 注册表和计划任务没有发生任何增删。
