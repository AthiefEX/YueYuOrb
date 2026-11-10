# 本机 Clash 登录自启去重

## 已确认的旧通知实验包延迟启动

2026-11-07实测分身登录约26秒后，Explorer启动了
`C:\Program Files\WindowsApps\YueYuOrb.Notifications_1.2.0.4_x64__t46e76va7zvsc\YueYuOrb.exe`。
该旧通知/弹幕实验包含独立的`windows.startupTask`扩展（TaskId为`YueYuOrbStartup`），不经过便携程序的Run守卫。只更新便携目录无法改变旧包行为。

已停用弹幕功能且仍安装此**精确旧版**时，可用Windows PowerShell先预览、再备份注销当前用户旧包：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\FloatDesk\scripts\remove-obsolete-notification-package.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File D:\FloatDesk\scripts\remove-obsolete-notification-package.ps1 -Apply
```

脚本只处理完整身份`YueYuOrb.Notifications_1.2.0.4_x64__t46e76va7zvsc`，不同版本拒绝操作；要求原MSIX身份匹配、复制校验后才注销。不移除当前便携程序、不修改FloatDesk配置、不批量注销其他用户的包。默认原MSIX来自`artifacts\danmaku-light`；可通过`-MsixPath`指定同身份恢复包。需要重新安装旧实验版本时可按恢复目录内`recovery.json`中的MSIX路径执行`Add-AppxPackage -Path`，这可能重新引入旧版启动问题。

本机恢复材料在`D:\FloatDesk\artifacts\obsolete-notification-recovery\20261107-005104`，不将本机包备份加入源码或发行包。

第三方修复仅针对已检查并经用户授权的 `Clash Verge (Admin)` 计划任务。此外，月语自己的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\FloatDesk` 自启入口也接入守卫。不会结束主桌面进程、改变代理配置、禁用原任务或修改其他应用的 Run/启动文件夹。

## 修复行为

原任务在每次登录（包括月语子会话登录）时执行 Clash。修复后保留原登录触发器、用户、最高权限、工作目录和原参数，将执行入口改为受保护的月语 Agent 启动守卫：

1. 如果执行发生在非当前控制台会话，跳过。
2. 如果主桌面已有相同完整路径的应用进程，跳过。
3. 否则按原参数启动程序。以每程序互斥锁避免守卫自身并发重复执行；无法核查进程时返回失败，不盲目启动。

守卫为无控制台窗口程序，不要求月语主界面或 Broker 服务一直运行。它仅处理经授权接入的启动任务；其他 Run 键、启动文件夹、服务和不同应用的启动入口仍需单独核查，不能宣称全部阻止。

## 月语自身的自启入口

安装新版会话服务后，月语自己的标准自启命令会改为 `Agent.exe --guard-startup-app "月语完整路径" -- --startup`，子会话直接跳过，不进入月语主程序。只转换已知的月语标准命令，未知参数或其他程序不会被改写。

守卫目录的 `startup-guard.v1` 记录已安装 Agent DLL 的 SHA256。月语保存开机启动设置时先核对此标记；没有匹配守卫时使用原直接启动格式。新版卸载流程先恢复月语自己的直接启动命令，再删除守卫；如入口被改动且仍依赖守卫则拒绝删除。已有 Clash 依赖仍须先按下文恢复。

尚未重启电脑验证首次登录；远程登录主桌面并非当前控制台会话时，此守卫也会跳过自启。

## 本机文件

- 原始任务备份和状态：`C:\Program Files\YueYuOrb\desktop-sessions\startup-guards`
- 守卫程序：`C:\Program Files\YueYuOrb\desktop-sessions\agent\YueYuOrb.SessionAgent.exe`
- 恢复脚本：`C:\Program Files\YueYuOrb\desktop-sessions\startup-guards\repair-clash-startup.ps1`

原任务备份没有包含在便携测试目录中，不把本机任务定义作为发行内容传播。修复脚本是本机任务适配工具，执行前校验任务指纹和当前用户，不自动应用到其他任务或机器。

## 恢复与卸载

在管理员 PowerShell 执行：

```powershell
$root = 'C:\Program Files\YueYuOrb\desktop-sessions\startup-guards'
$repair = Get-Content -LiteralPath (Join-Path $root 'Clash-Verge-Admin.active.json') -Raw | ConvertFrom-Json
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'repair-clash-startup.ps1') -Mode Restore -ExpectedHash $repair.AppliedSha256 -BackupPath $repair.Backup
```

恢复前会验证当前任务指纹；如果 Clash 更新或用户修改了任务，脚本拒绝覆盖，需重新检查当前配置。成功恢复后移除 active 标记，才可以卸载月语 Broker/Agent；新版卸载程序会在仍有任务依赖守卫时拒绝直接删除，避免破坏 Clash 自启。不要用旧版服务安装/卸载程序覆盖或删除守卫，旧版不认识此依赖。

Clash 重建其开机启动任务可能覆盖守卫，应重新检查；不能假定修复永久覆盖第三方重建行为。

## 验证边界

已用临时 Win32_ProcessStartTrace 订阅监视 Clash 新进程，连续启动真实子会话，确认登录任务运行、返回0、新增Clash进程为0且原Clash PID不变。代理服务和核心PID也保持不变。事件订阅在验证后清理，不安装永久监控。

未通过重启电脑实测一次完整的首次主桌面登录；“没有实例时允许启动”由守卫行为测试覆盖，不把它当作已重启验收。此修复不等于本机子会话已具备严格的“仅启动指定应用”环境。
