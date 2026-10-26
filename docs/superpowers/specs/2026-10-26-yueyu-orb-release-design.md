# 月语悬浮球品牌化与 GitHub 首发设计

日期：2026-10-26

## 目标

将当前 FloatDesk 桌面项目以“月语悬浮球”作为正式产品名发布到 GitHub：

- 用户可见名称统一为“月语悬浮球”。
- 关于页注明作者“七月风语”、GitHub 作者主页、项目仓库和 GPLv3。
- README 按 `readme-generator` 标准重写，并提供两张高质量视觉资产。
- 使用 GPL-3.0-only 开源。
- 生成 Windows 10/11 x64 便携版 ZIP 和未签名 Inno Setup 安装包。
- 将源码、标签 `v1.0.0`、便携包、安装包和 SHA256 校验文件发布到公开仓库。

## 非目标

- 不重写业务模块的 C# 命名空间、项目目录和程序集引用结构。
- 不迁移已有用户数据目录，避免破坏现有设置、笔记和 WebView2 profile。
- 不对二进制进行代码签名；Release 中明确标注“未签名测试发行版”。
- 不承诺 Windows 11、标准用户干净 VM、真实 IME 或 GPU 全屏等尚未完成的
  手工兼容性验收。

## 品牌与兼容性决策

### 产品身份

- 中文产品名：`月语悬浮球`
- 英文短名：`YueYuOrb`
- GitHub 仓库：`AthiefEX/YueYuOrb`
- 作者：`七月风语`
- 作者主页：`https://github.com/AthiefEX`
- 项目主页：`https://github.com/AthiefEX/YueYuOrb`
- 许可证：`GPL-3.0-only`

### 内部名称与发布名称

选择“用户可见品牌与二进制名称更新，内部命名空间保留”的方案。

保留：

- `FloatDesk.Core`、`FloatDesk.Windows`、`FloatDesk.Infrastructure` 和
  `FloatDesk.App` 项目名与 C# 命名空间。
- `%LOCALAPPDATA%\FloatDesk` 数据路径。它属于兼容性存储位置，不显示为产品名。
- 现有设置 schema、单实例协议的数据语义和测试夹具路径。

更换：

- WPF AssemblyName：`FloatDesk` → `YueYuOrb`。
- 可执行文件：`FloatDesk.exe` → `YueYuOrb.exe`。
- XAML pack URI：`/FloatDesk;component/...` → `/YueYuOrb;component/...`。
- 窗口标题、托盘、消息框、快捷面板、AI、笔记、截图和设置页中的产品名。
- 安装器 AppName、Publisher、目录名、快捷方式、输出文件名。
- 便携包和校验文件中的产品文件名。

未选择完整代码重命名，因为这会扩大 diff 并增加设置、资源 URI、测试和
安装升级风险，却没有额外用户价值。未选择只改显示名称，因为下载后的
`FloatDesk.exe` 会继续与正式品牌冲突。

## 用户界面与品牌资产

- 所有 `FD` 字样替换为“月”字品牌标记。
- 托盘图标改为月牙与圆形悬浮球的组合图形，不再依赖 `FD` 文本。
- 关于页展示：
  - 月语悬浮球 1.0.0
  - Windows 10/11 x64 桌面悬浮助手
  - 作者：七月风语
  - GitHub：可点击打开项目仓库
  - 许可证：GPL-3.0-only
- README 视觉采用月白、雾灰与暖金点缀，延续“月语”语义，不使用紫色霓虹。

## README 设计

README 采用 UI 产品展示模式，最多两张图片：

1. `assets/banner.webp`：月语悬浮球品牌封面。
2. `assets/features.webp`：三项核心结果图，聚焦悬浮轮盘、AI 网页和专注工具。

README 结构：

1. 产品名与一句话价值主张。
2. 最多三个徽章：GPLv3、Windows 10/11 x64、Release。
3. 这是什么与解决的问题。
4. 核心能力图。
5. 快速开始：便携版、安装版和源码构建。
6. 主要功能、系统要求、隐私和已知限制。
7. GPLv3、作者与 GitHub 链接。

所有技术验证状态与未完成兼容性验收保留在 `docs/`，README 只做可信摘要。

## 打包与发布

### 本地构建

- 继续使用 .NET 10 self-contained `win-x64` 发布。
- 便携 ZIP：
  `YueYuOrb-1.0.0-win-x64-portable.zip`
- 安装包：
  `YueYuOrb-1.0.0-win-x64-setup.exe`
- 校验文件：
  `SHA256SUMS.txt`

### 安装器

- 使用 Inno Setup 6 官方稳定版。
- 保持现有 AppId，允许从 FloatDesk 早期构建升级。
- `PrivilegesRequired=lowest`，安装到当前用户目录。
- 安装和卸载继续支持选择性清理 `%LOCALAPPDATA%\FloatDesk`。
- 安装包未签名，并在 Release 正文中明确提示 Windows SmartScreen 可能警告。

### GitHub

- 安装官方 GitHub CLI，完成一次浏览器登录。
- 创建公开仓库 `AthiefEX/YueYuOrb`，默认分支 `main`。
- 将当前已验证工作区提交为发布提交，并将发布提交合并到 `main`。
- 创建附注标签 `v1.0.0`。
- 创建 GitHub Release `v1.0.0`，上传便携 ZIP、安装包和 SHA256 文件。
- 仓库 Description 与 Topics 使用 README 生成流程给出的最终推荐。

## 实现影响

- Core：产品身份常量、作者、仓库和许可证信息。
- App：品牌字符串、月形标记、托盘图标、程序集名称和 XAML pack URI。
- Installer：产品名、发布者、EXE 名、目录、快捷方式和产物名。
- Scripts：便携包名、安装器调用和发布产物名。
- Docs：README、用户手册、架构、发布检查表和手工验收记录中的产品名。
- Tests：产品身份、关于页作者/GitHub/许可证、窗口标题和安装器配置回归测试。

## 验证

- 全部 Release 测试通过，0 失败。
- Release 构建 0 警告、0 错误。
- 便携版启动与单实例冒烟通过。
- 安装包完成静默安装、启动、卸载验证时，保留用户数据目录。
- 校验 ZIP、安装包和 EXE 的 SHA256。
- `git status` 只包含预期发布文件；构建产物不提交到源码树。

## 发布边界

虽然自动化、当前 Windows 10 22H2 冒烟与打包会验证，但 README 和 Release
必须保留以下未完成项：

- Windows 11 手工验收。
- Windows 10 21H2 与标准用户干净 VM。
- 真实输入法、DeepSeek 登录、独占全屏和混合 DPI 验证。
- 代码签名。
