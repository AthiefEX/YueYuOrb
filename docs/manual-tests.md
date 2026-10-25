# FloatDesk 手工验收记录

日期：2026-10-25
版本：1.0.0
当前状态：**待发布验收**

状态只使用 PASS / FAIL / NOT TESTED / BLOCKED / N/A。自动化测试不能代替输入法、网页登录、截图交互、安装器和真实显示器验证。

## 已完成证据

| 项目 | 环境 | 结果 | 证据 |
|---|---|---|---|
| Windows 10 22H2 x64 启动 | 19045，管理员交互会话 | PASS | 进程持续存活，日志出现 `startup.begin` 和 `startup.complete` |
| Release 自包含包启动 | 19045，管理员交互会话 | PASS | 最终 `artifacts/publish/win-x64/FloatDesk.exe` 持续存活，日志出现 `startup.complete` |
| 单实例交接 | 19045，同一会话 | PASS | 第二进程静默唤起首实例，5 秒内以退出码 0 结束 |
| Release 单实例交接 | 19045，同一会话 | PASS | 最终发布包第二进程 5 秒内以退出码 0 结束，首实例存活 |
| 前台热键冲突自动备用 | 19045；`Ctrl+Alt+A` 被其他程序占用 | PASS | 实际绑定 `Ctrl+Alt+Shift+A`，日志记录 `hotkey.initial.fallback`，托盘提示与快捷面板 ToolTip 显示备用键 |
| 设置页悬停与应用 | Windows 10 22H2 x64，管理员交互会话 | PASS | 鼠标悬停并点击“应用”后进程存活，日志无 `hotkey.rebind.failed` |
| 快捷面板框选与编辑器 | Windows 10 22H2 x64，双屏 2560×1440 + 800×600 | PASS | 两个覆盖层正常显示；拖动生成 500×340 选区，Enter 打开 `FloatDesk 截图标注` |
| 备用截图快捷键实机触发 | Windows 10 22H2 x64 | PASS | `Ctrl+Alt+Shift+A` 实际打开双屏框选覆盖层，Esc 取消后会话释放 |
| 崩溃回归事件 | 上述真实 UI 验证时段 | PASS | 测试后 `Application/.NET Runtime` 无新增 FloatDesk 进程终止事件 |
| 自动化测试 | Windows 10 22H2 x64 | PASS | 58/58，Debug；Core 28、Infrastructure 12、Windows 6、UI 12 |
| 跨屏负坐标截图裁剪 | 自动化像素夹具 | PASS | `(-100,50)-(300,250)` 输出 400×200 |
| 自定义图片图层 | WPF 窗口级测试 | PASS | 自定义 PNG 隐藏默认渐变圆与 `FD` 文本 |
| 笔记保存失败回退 | WPF 窗口级测试 | PASS | 切换笔记保存失败后恢复原选择，未保存正文保留 |
| 保存失败阻止删除 | WPF 窗口级测试 | PASS | 保存失败时不调用回收站操作，未保存正文保留 |
| 截图覆盖层关闭恢复 | WPF 窗口级测试 | PASS | 关闭覆盖层发出取消信号，截图会话可清理 |
| 截图输出路径异常 | WPF 窗口级测试 | PASS | 无效自动保存目录在编辑器内显示失败，窗口不关闭、进程不崩溃 |
| 旧前台状态隔离 | Core 单元测试 | PASS | 旧 epoch 的 Idle 状态不能覆盖当前 Verified 状态 |

## 必测环境

| 环境 | 系统 | 状态 |
|---|---|---|
| E01 | Windows 10 21H2 19044 x64，标准用户 | NOT TESTED |
| E02 | Windows 10 22H2 19045 x64，标准用户 | PARTIAL：启动与单实例 PASS；交互功能未全测 |
| E03 | Windows 11 24H2/25H2 x64 | NOT TESTED |
| E04 | 双屏 100% + 150%，副屏在左 | NOT TESTED |
| E05 | Windows 10 干净 VM，无 WebView2 Runtime | NOT TESTED |
| E06 | 无网络、只读输出目录 | NOT TESTED |
| E07 | 同用户第二 Windows 会话 | NOT TESTED |

## 功能清单

| ID | 项目 | 状态 | 备注 |
|---|---|---|---|
| A01 | 关闭工具窗后后台/托盘/球继续运行 | NOT TESTED | 需交互桌面验收 |
| A02 | 第二实例唤起首实例后退出 | PASS | 自动化冒烟 |
| A03 | 跨会话拒绝共享数据 | NOT TESTED | 需第二会话 |
| A04 | 托盘退出后热键与进程清理 | NOT TESTED | 冒烟末尾使用强制结束 |
| A05 | 开机启动开关与空格路径 | NOT TESTED | 未写用户启动项 |
| A06 | 热键冲突自动备用、托盘与面板可用 | PASS | 初始冲突自动切换并提示；修改配置冲突仍保留原绑定的自动化测试 PASS |
| A07 | Explorer 重启、睡眠唤醒恢复 | NOT TESTED | 需交互 |
| A08-A12 | 球点击/拖动/多屏/图片/时间 | NOT TESTED | 图片图层自动测试 PASS |
| A13-A16 | 普通置顶、全屏、游戏、受保护界面 | NOT TESTED | 无目标游戏环境和实机显示矩阵 |
| A17-A18 | exe/lnk/文件夹、参数、异常路径 | PARTIAL | 参数构造自动测试 PASS；UI 启动未测 |
| A19-A27 | 输入法请求、读回、规则、快速切窗、中文豁免 | NOT TESTED | 纯逻辑测试 PASS；需真实 IME 目标 |
| A28-A33 | AI 按需启动、DeepSeek、站点、OAuth、崩溃恢复 | NOT TESTED | WebView2 交互未实测 |
| A34-A37 | 记事本完整流程、冲突、1 MiB | PARTIAL | 仓储与失败回退测试 PASS；真实 UI 输入法未测 |
| A38-A44 | 选区、窗口、屏幕、倒计时、DPI、取消恢复 | PARTIAL | 双屏框选、拖动、Enter 确认和 Esc 取消 PASS；窗口/屏幕截图与倒计时仍待测 |
| A45-A49 | 矩形/箭头/中文、剪贴板、保存 | PARTIAL | 标注文档与导出工件测试 PASS；粘贴未实测 |
| A50 | 40M 像素/512 MiB 预算 | PASS | 代码边界与异常路径检查 |
| A51 | 全黑真实桌面、受保护内容 | NOT TESTED | 需实机 |
| A52 | 配置主/备损坏回退 | PASS | 自动测试 |
| A53 | 应用/取消/导入导出/未来 schema/并发 | PARTIAL | 字段合并、只读和写失败自动测试 PASS |
| A54 | 键盘、高对比度、字体回退 | NOT TESTED | 需辅助技术验收 |
| A55 | 8 小时/100 次长期稳定性 | NOT TESTED | 未执行 |
| A56 | 安装、升级、卸载 | NOT TESTED | Inno Setup 未安装，安装包未生成 |
| A57 | 无 .NET SDK 的自包含启动 | NOT TESTED | 待干净 VM |
| A58 | 日志不含敏感正文 | PASS | 代码审计；日志仅事件名和有限错误码 |

## 输入法目标记录

| 目标 | 布局与 IME | 状态 | 实际读回/耗时 |
|---|---|---|---|
| 系统记事本 | 中文 IME + US | NOT TESTED | |
| Edge 普通文本框 | 同上 | NOT TESTED | |
| VS Code | 同上 | NOT TESTED | |
| Windows Terminal | 同上 | NOT TESTED | |
| 第三方 IME | 用户实际版本 | NOT TESTED | |
| 管理员应用 | 提权目标 | NOT TESTED | |
| FloatDesk 设置/AI/笔记/标注 | 手动锁定期间中文 | NOT TESTED | |

## 全屏与游戏记录

| 目标 | 渲染/窗口模式 | 球可见 | 焦点影响 | 截图 | 状态 |
|---|---|---|---|---|---|
| Edge F11 | 待测 | 待测 | 待测 | 待测 | NOT TESTED |
| 无边框游戏 | 待测 | 待测 | 待测 | 待测 | NOT TESTED |
| 独占全屏游戏 | 待测 | 不承诺 | 不得抢焦点 | 不承诺 | NOT TESTED |

## 性能记录

冷启动、5 分钟静置、100 次面板、20 次选区截图、100 次截图句柄/内存和 8 小时运行均为 NOT TESTED。发布前须在 Release x64、无调试器环境采集。
