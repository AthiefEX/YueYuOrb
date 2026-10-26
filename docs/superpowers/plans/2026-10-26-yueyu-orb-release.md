# 月语悬浮球品牌化与 GitHub 首发实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 FloatDesk 以“月语悬浮球 / YueYuOrb”品牌完成 Windows x64 首发，生成便携包和安装包并发布到 GitHub。

**Architecture:** 保留现有 `FloatDesk.*` 命名空间、项目结构和 `%LOCALAPPDATA%\FloatDesk` 数据路径，只替换产品身份、用户可见品牌、程序集/可执行文件名和发行配置。品牌常量集中在 Core，关于页与工具窗口使用同一组常量；打包脚本和 Inno Setup 只消费最终 `YueYuOrb.exe`。

**Tech Stack:** .NET 10 WPF, C# 13, xUnit, PowerShell, Inno Setup 6, GitHub CLI

**Spec:** `docs/superpowers/specs/2026-10-26-yueyu-orb-release-design.md`

## Global Constraints

- 中文产品名固定为 `月语悬浮球`。
- 英文短名、程序集名和 EXE 名固定为 `YueYuOrb`。
- 作者固定为 `七月风语`，作者主页固定为 `https://github.com/AthiefEX`。
- 项目仓库固定为 `https://github.com/AthiefEX/YueYuOrb`。
- 许可证固定为 `GPL-3.0-only`，仓库根目录必须包含完整 GPLv3 `LICENSE`。
- 保留 `FloatDesk.Core`、`FloatDesk.Windows`、`FloatDesk.Infrastructure`、`FloatDesk.App` 命名空间和项目名。
- 保留 `%LOCALAPPDATA%\FloatDesk` 数据路径和安装器 AppId `CB89B2C1-41C1-47A6-8B73-CF54C6D0D403`。
- 支持 Windows 10/11 x64；不新增平台专属依赖。
- README 最多使用三个徽章和两张图片，文件名固定为 `assets/banner.webp`、`assets/features.webp`。
- 发行产物固定为 `YueYuOrb-1.0.0-win-x64-portable.zip`、`YueYuOrb-1.0.0-win-x64-setup.exe`、`SHA256SUMS.txt`。
- 安装包未签名；发布说明必须提示 SmartScreen，并保留尚未完成的人工兼容性验收。

## Review Focus

- 中文品牌字样必须在 UTF-8 XAML、PowerShell 和 Inno Setup 中保持不变，不能出现乱码或回退成 `FloatDesk`。
- `/YueYuOrb;component/...` 必须与 WPF `AssemblyName` 一致，否则工具窗口运行时会在加载资源字典时失败。
- 升级与卸载必须继续使用原 AppId 和数据目录，不能删除用户设置、笔记、截图或 WebView2 profile。
- 可执行文件改名后，安装、启动、单实例冒烟、快捷方式和卸载图标必须全部指向 `YueYuOrb.exe`。
- README 图片必须可读、无伪文字、总量克制，并以 WebP 形式提交；不能把概念图误写成真实兼容性证明。

---

### Task 1: 产品身份常量与发布配置门禁

**Files:**
- Modify: `src/FloatDesk.Core/ProductInfo.cs`
- Modify: `tests/FloatDesk.Core.Tests/BuildBaselineTests.cs`
- Create: `tests/FloatDesk.Ui.Tests/ReleaseConfigurationTests.cs`
- Modify: `src/FloatDesk.App/FloatDesk.App.csproj`
- Modify: `installer/FloatDesk.iss`
- Modify: `scripts/publish.ps1`

**Interfaces:**
- Produces: `ProductInfo.Name: string`, `ProductInfo.EnglishName: string`, `ProductInfo.Version: string`, `ProductInfo.Author: string`, `ProductInfo.AuthorUrl: string`, `ProductInfo.RepositoryUrl: string`, `ProductInfo.License: string`
- Produces: release configuration file assertions used by later packaging tasks.

- [ ] **Step 1: Write failing product identity and release configuration tests**

Replace the identity assertion with:

```csharp
Assert.Equal("月语悬浮球", ProductInfo.Name);
Assert.Equal("YueYuOrb", ProductInfo.EnglishName);
Assert.Equal("1.0.0", ProductInfo.Version);
Assert.Equal("七月风语", ProductInfo.Author);
Assert.Equal("https://github.com/AthiefEX", ProductInfo.AuthorUrl);
Assert.Equal("https://github.com/AthiefEX/YueYuOrb", ProductInfo.RepositoryUrl);
Assert.Equal("GPL-3.0-only", ProductInfo.License);
```

Add `ReleaseConfigurationTests` asserting:

```csharp
Assert.Contains("<AssemblyName>YueYuOrb</AssemblyName>", appProject);
Assert.Contains("YueYuOrb-1.0.0-win-x64-portable.zip", publishScript);
Assert.Contains("YueYuOrb-1.0.0-win-x64-setup.exe", installer);
Assert.Contains("AppId={{CB89B2C1-41C1-47A6-8B73-CF54C6D0D403}", installer);
Assert.Contains(@"\{localappdata}\FloatDesk", installer);
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/FloatDesk.Core.Tests/FloatDesk.Core.Tests.csproj -c Release --filter ProductIdentity_is_stable`

Expected: FAIL because `EnglishName` does not exist and `Name` is still `FloatDesk`.

- [ ] **Step 3: Add the identity constants and rename release entry points**

Set the exact constants from Global Constraints. Change WPF `AssemblyName` to `YueYuOrb`, portable ZIP and EXE references to `YueYuOrb-*`/`YueYuOrb.exe`, and installer display/product names to `月语悬浮球` while preserving AppId and data path.

- [ ] **Step 4: Run the focused tests**

Run: `dotnet test tests/FloatDesk.Core.Tests/FloatDesk.Core.Tests.csproj -c Release --filter FullyQualifiedName~BuildBaselineTests`

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Release --filter FullyQualifiedName~ReleaseConfigurationTests`

Expected: PASS.

- [ ] **Step 5: Commit the identity gate**

```powershell
git add src/FloatDesk.Core/ProductInfo.cs src/FloatDesk.App/FloatDesk.App.csproj tests/FloatDesk.Core.Tests/BuildBaselineTests.cs tests/FloatDesk.Ui.Tests/ReleaseConfigurationTests.cs installer/FloatDesk.iss scripts/publish.ps1
git commit -m "feat: establish YueYuOrb product identity"
```

---

### Task 2: 可见品牌、程序集资源和工具窗口

**Files:**
- Modify: `src/FloatDesk.App/App.xaml.cs`
- Modify: `src/FloatDesk.App/AppRuntime.cs`
- Modify: `src/FloatDesk.App/Bootstrap/TrayService.cs`
- Modify: `src/FloatDesk.App/Views/OrbWindow.xaml`
- Modify: `src/FloatDesk.App/Views/QuickPanelWindow.xaml`
- Modify: `src/FloatDesk.App/Views/SettingsWindow.xaml`
- Modify: `src/FloatDesk.App/Views/NotesWindow.xaml`
- Modify: `src/FloatDesk.App/Views/AiBrowserWindow.xaml`
- Modify: `src/FloatDesk.App/Views/CaptureEditorWindow.xaml`
- Modify: `src/FloatDesk.App/Views/*.xaml.cs`
- Modify: `tests/FloatDesk.Ui.Tests/ToolWindowVisualTests.cs`
- Modify: `tests/FloatDesk.Ui.Tests/OrbWindowImageTests.cs`

**Interfaces:**
- Consumes: `ProductInfo.*` from Task 1.
- Produces: user-visible product name `月语悬浮球`, fallback mark `月`, executable `YueYuOrb.exe`, and `/YueYuOrb;component/...` pack URIs.

- [ ] **Step 1: Write failing branding and pack URI tests**

Add assertions that every WPF window XAML:

```csharp
Assert.DoesNotContain("Title=\"FloatDesk", xaml, StringComparison.Ordinal);
Assert.DoesNotContain("Source=\"/FloatDesk;component/", xaml, StringComparison.Ordinal);
```

Assert shared-resource consumers contain:

```csharp
Assert.Contains("Source=\"/YueYuOrb;component/Resources/SettingsWindowResources.xaml\"", xaml);
```

Assert `OrbWindow.xaml` has `Text="月"` and `TrayService.cs` no longer contains `DrawString("FD"`.

- [ ] **Step 2: Run the UI tests to verify they fail**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Release --filter "FullyQualifiedName~ToolWindowVisualTests|FullyQualifiedName~OrbWindowImageTests"`

Expected: FAIL on the old titles, `FD`, and `/FloatDesk;component/` pack URIs.

- [ ] **Step 3: Apply visible branding and resource URI changes**

Replace user-facing `FloatDesk` with `月语悬浮球`, or `YueYuOrb` where an English/executable/file name is required. Replace capsule/fallback `FD` marks with `月`. Update all tool-window titles, dialogs, tray text, AI theme labels, hotkey messages, import/export labels, note titles, capture titles, and screenshot fallback titles. Keep internal namespaces, mutex/pipe names, registry value name, log event names, install data path, and image export folder unchanged.

Change `AppIcon.Create()` to draw a warm-gold crescent and pale orb with no `FD` text. Ensure the generated icon uses only Windows drawing APIs already available.

- [ ] **Step 4: Run the focused tests**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Release --filter "FullyQualifiedName~ToolWindowVisualTests|FullyQualifiedName~OrbWindowImageTests"`

Expected: PASS.

- [ ] **Step 5: Commit visible branding**

```powershell
git add src/FloatDesk.App tests/FloatDesk.Ui.Tests/ToolWindowVisualTests.cs tests/FloatDesk.Ui.Tests/OrbWindowImageTests.cs
git commit -m "feat: apply YueYuOrb visible branding"
```

---

### Task 3: 关于页、GPLv3 与项目文档

**Files:**
- Modify: `src/FloatDesk.App/Views/SettingsWindow.xaml`
- Modify: `src/FloatDesk.App/Views/SettingsWindow.xaml.cs`
- Modify: `tests/FloatDesk.Ui.Tests/SettingsWindowVisualTests.cs`
- Create: `LICENSE`
- Modify: `docs/user-guide.md`
- Modify: `docs/architecture.md`
- Modify: `docs/progress.md`
- Modify: `docs/release-checklist.md`
- Modify: `docs/compatibility.md`

**Interfaces:**
- Consumes: `ProductInfo.Author`, `ProductInfo.AuthorUrl`, `ProductInfo.RepositoryUrl`, `ProductInfo.License`.
- Produces: clickable About-page project link and GPLv3 repository metadata.

- [ ] **Step 1: Write failing About-page tests**

Assert normalized XAML and code-behind contain:

```csharp
Assert.Contains("月语悬浮球 1.0.0", xaml);
Assert.Contains("作者：七月风语", xaml);
Assert.Contains("GPL-3.0-only", xaml);
Assert.Contains("ProjectRepositoryLink", xaml);
Assert.Contains("Process.Start", settingsCode);
Assert.Contains("https://github.com/AthiefEX/YueYuOrb", settingsCode);
```

Assert `LICENSE` exists, starts with `GNU GENERAL PUBLIC LICENSE`, and contains `Version 3, 29 June 2007`.

- [ ] **Step 2: Run the About-page tests to verify they fail**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Release --filter FullyQualifiedName~SettingsWindowVisualTests`

Expected: FAIL because author, GPL, repository link, and `LICENSE` are absent.

- [ ] **Step 3: Implement About page and official GPLv3**

Add `作者：七月风语`, `GitHub：AthiefEX/YueYuOrb`, and `GPL-3.0-only` to the About card. Add a button or hyperlink named `ProjectRepositoryLink` that launches the exact repository URL with `Process.Start` and `UseShellExecute = true`. Include the official unmodified GPLv3 text in `LICENSE`.

Update docs to use `月语悬浮球`, `YueYuOrb.exe`, final artifact names, accurate test counts, GPL-3.0-only, and retained compatibility gaps.

- [ ] **Step 4: Run the focused tests**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Release --filter FullyQualifiedName~SettingsWindowVisualTests`

Expected: PASS.

- [ ] **Step 5: Commit About page and licensing**

```powershell
git add LICENSE src/FloatDesk.App/Views/SettingsWindow.xaml src/FloatDesk.App/Views/SettingsWindow.xaml.cs tests/FloatDesk.Ui.Tests/SettingsWindowVisualTests.cs docs
git commit -m "feat: add YueYuOrb about page and GPLv3"
```

---

### Task 4: README 作品化与两张视觉资产

**Files:**
- Modify: `README.md`
- Create: `assets/banner.webp`
- Create: `assets/features.webp`

**Interfaces:**
- Consumes: product facts and compatibility limits from Tasks 1-3.
- Produces: README that begins with `月语悬浮球`, contains no more than three badges and exactly two image links.

- [ ] **Step 1: Prepare the two visual assets**

Use the generated concept images as the visual bases:

- `C:\Users\Administrator\Documents\Codex\2026-10-26\diff-d-floatdesk-floatdesk-feat-settings\work\readme-preview\banner-concept.png`
- `C:\Users\Administrator\Documents\Codex\2026-10-26\diff-d-floatdesk-floatdesk-feat-settings\work\readme-preview\features-concept.png`

Add exact, large, readable typography programmatically:

- Banner: `月语悬浮球`, `把常用工具留在指尖`, `悬浮球 / AI 网页 / 专注工具`.
- Features: `随手展开`, `专注网页`, `本地记录`, with no more than one short supporting line per card.

Keep the deep-black, white/gray/warm-gold direction and large negative space. Save final assets as:

```text
assets/banner.webp
assets/features.webp
```

- [ ] **Step 2: Verify the assets before writing README**

Run:

```powershell
Add-Type -AssemblyName System.Drawing
$files = @('assets/banner.webp','assets/features.webp')
foreach ($file in $files) {
  if (-not (Test-Path -LiteralPath $file)) { throw "Missing $file" }
  if ((Get-Item -LiteralPath $file).Length -lt 30000) { throw "Asset too small: $file" }
}
```

Expected: both files exist, exceed 30 KB, and are visually inspected for correct Chinese text and no pseudo-text.

- [ ] **Step 3: Rewrite README**

Use this order:

1. H1 `月语悬浮球` and one-sentence value proposition.
2. Three badges: GPLv3, Windows 10/11 x64, v1.0.0.
3. `assets/banner.webp`.
4. What it is and the problem it solves.
5. `assets/features.webp` plus three result-oriented capabilities.
6. Portable ZIP, unsigned installer, and source-build quick start.
7. Requirements, privacy, compatibility limits, and documentation links.
8. GPLv3, author `七月风语`, and GitHub links.

Do not present unverified Windows 11/IME/DeepSeek/fullscreen/manual tests as passed.

- [ ] **Step 4: Verify README constraints**

Run:

```powershell
$readme = Get-Content -Raw README.md
if (($readme | Select-String -Pattern 'assets/banner.webp' -AllMatches).Matches.Count -ne 1) { throw 'banner count' }
if (($readme | Select-String -Pattern 'assets/features.webp' -AllMatches).Matches.Count -ne 1) { throw 'features count' }
if (($readme | Select-String -Pattern 'img.shields.io' -AllMatches).Matches.Count -gt 3) { throw 'too many badges' }
if (-not $readme.Contains('GPL-3.0-only')) { throw 'license missing' }
```

Expected: no exception.

- [ ] **Step 5: Commit README and assets**

```powershell
git add README.md assets/banner.webp assets/features.webp
git commit -m "docs: launch YueYuOrb README"
```

---

### Task 5: 便携包、安装包与哈希清单

**Files:**
- Modify: `scripts/publish.ps1`
- Modify: `installer/FloatDesk.iss`
- Modify: `scripts/smoke.ps1`
- Modify: `tests/FloatDesk.Ui.Tests/ReleaseConfigurationTests.cs`

**Interfaces:**
- Consumes: `YueYuOrb.exe` from Task 1 and visible branding from Task 2.
- Produces: portable ZIP, unsigned setup EXE, and `SHA256SUMS.txt`.

- [ ] **Step 1: Extend failing release configuration tests**

Assert:

```csharp
Assert.Contains("YueYuOrb.exe", pumpScript);
Assert.Contains("YueYuOrb-1.0.0-win-x64-setup.exe", publishScript);
Assert.Contains("SHA256SUMS.txt", publishScript);
Assert.Contains("PrivilegesRequired=lowest", installer);
Assert.Contains("Name: \"chinesesimp\"", installer);
Assert.Contains("YueYuOrb-{#AppVersion}-win-x64-setup", installer);
```

- [ ] **Step 2: Run release configuration tests to verify they fail**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Release --filter FullyQualifiedName~ReleaseConfigurationTests`

Expected: FAIL on old smoke/installer artifact names.

- [ ] **Step 3: Finalize packaging**

Make `publish.ps1` produce:

```text
artifacts/publish/win-x64/YueYuOrb.exe
artifacts/YueYuOrb-1.0.0-win-x64-portable.zip
artifacts/SHA256SUMS.txt
```

Add an `-Installer` switch that checks for `iscc`, invokes Inno Setup with `AppVersion`, and adds setup hash only when the installer exists. Make the installer produce `YueYuOrb-1.0.0-win-x64-setup.exe`, use `月语悬浮球` for visible names, install into `{localappdata}\Programs\YueYuOrb`, retain `%LOCALAPPDATA%\FloatDesk` data cleanup, and keep the existing AppId. Update smoke defaults/process checks to `YueYuOrb`.

- [ ] **Step 4: Run release configuration tests**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Release --filter FullyQualifiedName~ReleaseConfigurationTests`

Expected: PASS.

- [ ] **Step 5: Commit packaging**

```powershell
git add scripts/publish.ps1 scripts/smoke.ps1 installer/FloatDesk.iss tests/FloatDesk.Ui.Tests/ReleaseConfigurationTests.cs
git commit -m "build: package YueYuOrb release artifacts"
```

---

### Task 6: 全量验证、发布物与 GitHub 首发

**Files:**
- Modify only if verification exposes defects.
- Generate: `artifacts/` outputs, not committed.

**Interfaces:**
- Consumes: all prior tasks.
- Produces: verified source commit, `main`, annotated tag `v1.0.0`, public repository, and Release assets.

- [ ] **Step 1: Run the full Release test and locked build gate**

Run:

```powershell
.\scripts\test.ps1 -Configuration Release
dotnet restore FloatDesk.sln --locked-mode
dotnet build FloatDesk.sln -c Release --no-restore
```

Expected: all tests pass; build reports `0 Warning(s)` and `0 Error(s)`.

- [ ] **Step 2: Produce and verify release artifacts**

Run:

```powershell
.\scripts\publish.ps1 -Version 1.0.0 -Installer
.\scripts\smoke.ps1 -Executable .\artifacts\publish\win-x64\YueYuOrb.exe
Get-FileHash -Algorithm SHA256 .\artifacts\YueYuOrb-1.0.0-win-x64-portable.zip
Get-FileHash -Algorithm SHA256 .\artifacts\YueYuOrb-1.0.0-win-x64-setup.exe
```

Expected: ZIP, setup, and `SHA256SUMS.txt` exist; hashes match; smoke reports startup, primary alive, and second instance exit code 0.

- [ ] **Step 3: Verify installer behavior**

Run a silent install to the current user, launch installed `YueYuOrb.exe`, verify single instance, uninstall while preserving `%LOCALAPPDATA%\FloatDesk`, then repeat with the explicit cleanup task in a disposable test directory if safe.

Expected: installed shortcut and EXE point to `YueYuOrb`, uninstall removes program files, default uninstall preserves user data.

- [ ] **Step 4: Perform final source and history review**

Run:

```powershell
git status --short
git diff --check
rg -n '/FloatDesk;component|Title="FloatDesk|\bFloatDesk-\$Version|FloatDesk-1\.0\.0|FloatDesk\.exe' src scripts installer README.md docs tests
```

Expected: no unexpected build output; no stale visible names or pack URIs; remaining internal namespace/data-path mentions are intentional.

- [ ] **Step 5: Install and authenticate GitHub CLI**

Install official GitHub CLI with `winget`. Run `gh auth login --web --git-protocol https`, complete browser login as `AthiefEX`, then:

```powershell
gh auth status
gh repo view AthiefEX/YueYuOrb
```

Expected: authenticated account is `AthiefEX`; if the repository does not yet exist, `gh repo view` reports not found, which is acceptable for the create step.

- [ ] **Step 6: Create the public repository and publish source**

```powershell
gh repo create AthiefEX/YueYuOrb --public --description "A Windows 10/11 floating orb for quick launchers, AI web apps, notes, capture, and keyboard-layout rules."
git remote add origin https://github.com/AthiefEX/YueYuOrb.git
git push -u origin feat/settings-visual-redesign
git push origin feat/settings-visual-redesign:main
gh repo edit AthiefEX/YueYuOrb --default-branch main
```

Add final recommended topics with `gh repo edit --add-topic`. Expected: repository is public, `main` contains the verified release commit, and default branch is `main`.

- [ ] **Step 7: Create annotated tag and Release**

```powershell
git tag -a v1.0.0 -m "月语悬浮球 v1.0.0"
git push origin v1.0.0
gh release create v1.0.0 `
  .\artifacts\YueYuOrb-1.0.0-win-x64-portable.zip `
  .\artifacts\YueYuOrb-1.0.0-win-x64-setup.exe `
  .\artifacts\SHA256SUMS.txt `
  --title "月语悬浮球 v1.0.0" `
  --notes-file .\docs\release-notes-v1.0.0.md
```

Create Release notes that list features, download choices, SHA256 verification, GPLv3, Windows 10/11 x64 scope, unsigned-package SmartScreen warning, and the unverified manual-test boundary.

- [ ] **Step 8: Verify the published release**

```powershell
gh release view v1.0.0 --json tagName,name,isDraft,isPrerelease,assets,url
git status --short --branch
```

Expected: tag `v1.0.0`; Release is public, not draft, not prerelease; all three assets are present with nonzero sizes; local branch is clean except intentional ignored/disposable files.
