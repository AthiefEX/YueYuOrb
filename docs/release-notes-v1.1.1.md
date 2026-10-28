# 月语悬浮球 v1.1.1

发布日期：2026-10-28

本次更新让快捷应用更容易添加和辨认：将 EXE 或 Windows 快捷方式拖入设置即可添加，设置列表、轮盘和快捷应用栏自动显示真实应用图标；轮盘下方快捷应用栏可以单独隐藏。

## 下载

| 方式 | 文件 | 使用说明 |
|---|---|---|
| 便携版 | [YueYuOrb-1.1.1-win-x64-portable.zip](https://github.com/AthiefEX/YueYuOrb/releases/download/v1.1.1/YueYuOrb-1.1.1-win-x64-portable.zip) | 解压后运行 `YueYuOrb.exe` |
| 安装版 | [YueYuOrb-1.1.1-win-x64-setup.exe](https://github.com/AthiefEX/YueYuOrb/releases/download/v1.1.1/YueYuOrb-1.1.1-win-x64-setup.exe) | 当前用户安装，可覆盖升级 |
| 校验值 | [SHA256SUMS.txt](https://github.com/AthiefEX/YueYuOrb/releases/download/v1.1.1/SHA256SUMS.txt) | 核对便携包、安装包与发布 EXE |

发行包包含 .NET 10 win-x64 运行环境，无需另装 .NET SDK 或 Desktop Runtime。安装包尚未进行代码签名，Windows SmartScreen 可能提示“未知发布者”。

## 新增功能

- **拖放添加快捷应用**：将一个或多个 `.exe`、`.lnk` 文件拖到“设置 → 快捷应用”页面，自动识别名称、路径和类型。重复路径会跳过，不支持或读取失败的文件会显示中文反馈，其余有效项目仍可添加。点击“应用”后保存。
- **真实应用图标**：设置列表、轮盘中的快捷应用和下方快捷应用栏自动读取 Windows Shell 图标，保留透明背景，并在本机缓存。自定义快捷方式图标也可识别。
- **快捷应用栏开关**：在轮盘设置中切换“显示轮盘下方快捷应用栏”，应用后即可生效。关闭只隐藏下方栏，不删除应用，也不改变应用是否加入轮盘。

## 修复与优化

- 修复 Shell 图标透明通道丢失后出现黑色方块的问题。
- 修复图标控件加载时清空已就绪图像造成的短暂闪烁；提取失败时显示快捷应用占位图标。
- 损坏或截断的图标缓存会回退到重新提取，不影响应用列表使用。
- 图标元数据读取、快捷方式解析、预热及清理在后台执行，避免远程路径或较慢磁盘阻塞设置页和轮盘。
- 快捷方式目标程序、自定义图标源或图标索引更新后重新生成对应缓存；清理缓存时保留近期临时文件，降低与写入冲突的风险。
- 修复拖放识别尚未完成时取消、应用、重新加载设置或关闭窗口，迟到结果仍写入列表的问题；完成添加前按当前列表再次去重并重新排序。
- “关于”页与产品版本号同步为 `1.1.1`。
- 发布脚本采用隔离编译目录，避免正在运行的开发版程序锁住 DLL 而导致打包失败。

## 升级与兼容性

- 兼容目标为 Windows 10 21H2/22H2 x64、Windows 11 x64；安装器最低版本仍为 Windows 10 21H2。
- 从 `v1.1.0` 升级无需手动迁移设置。旧设置默认显示快捷应用栏，原有快捷应用、轮盘、笔记及翻译配置继续保留。
- `.lnk` 仍按原快捷方式启动，保留其参数与工作目录。拖放和图标识别不会启动目标程序。
- 图标 PNG 保存在 `%LOCALAPPDATA%\FloatDesk\launcher-icons`，不包含在设置导出文件中。
- “便携版”指无需安装；用户配置仍沿用 `%LOCALAPPDATA%\FloatDesk` 数据目录。
- 本版包含 `v1.1.0` 已有的三种翻译模式、透明字幕、图片裁切和图片即时刷新功能，详见 [v1.1.0 更新说明](https://github.com/AthiefEX/YueYuOrb/blob/v1.1.1/docs/release-notes-v1.1.0.md)。

## 验证记录

- Release 构建：0 警告、0 错误。
- 自动化测试：666/666 通过（Core 144、Infrastructure 93、Windows 49、UI 380）。
- 独立代码复审：未发现 Critical 或 Important 问题。

Windows 11、真实第三方应用拖放与启动、混合 DPI，以及浅色/深色/高对比度组合仍需人工验收。本次保留已在运行的程序，未停止旧实例执行新版独立主实例启动或安装、升级、卸载冒烟。取消正在识别的拖放后，重新打开设置页可能暂时保留“正在识别”的提示文字，不影响列表数据。

## 开源许可

作者：七月风语。项目：[AthiefEX/YueYuOrb](https://github.com/AthiefEX/YueYuOrb)。

使用 [GNU General Public License v3.0 only](https://github.com/AthiefEX/YueYuOrb/blob/v1.1.1/LICENSE) 开源。

## SHA256

```text
1e09f57998e1a05c93e7100fc0f1e96c06baa8637f274dcbd925bf2b3c217d0c  publish/win-x64/YueYuOrb.exe
5184d69b1858c88924ac9a689afd9d046ad922fc2c9ea270a26e1f8d5d4a2056  YueYuOrb-1.1.1-win-x64-portable.zip
6834599ec96d69c2271a91de6debfc1ded9207d491ab845edaa63c5f51f30746  YueYuOrb-1.1.1-win-x64-setup.exe
```
