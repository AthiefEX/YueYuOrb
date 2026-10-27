# 快捷应用图标自动识别与拖放导入 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让快捷应用支持拖入 exe/lnk 自动添加、显示真实应用图标，并可在设置中控制轮盘下方快捷应用栏是否显示。

**Architecture:** 设置模型增加应用坞显示开关；Core 提供纯拖放分类器，Windows 层用 Shell 只读解析快捷方式并提取图标，App 层持久缓存已冻结的 WPF 图像。设置列表、轮盘和底部应用坞统一通过异步图标呈现控件读取缓存，失败时保留现有占位图标。

**Tech Stack:** .NET 10、WPF、Windows Shell COM/PInvoke、System.Drawing.Common、CommunityToolkit.Mvvm、xUnit

**Spec:** `docs/superpowers/specs/2026-10-27-launcher-icon-import-dock-visibility-design.md`

## Global Constraints

- 仅面向 Windows 10 22H2 x64 和 Windows 11 x64。
- 设置 `SchemaVersion` 保持 `1`；旧 JSON 缺少 `ShowLauncherDock` 时必须得到 `true`。
- 拖放只接受现有 `.exe`、`.lnk` 文件；拖放和图标解析不得执行目标程序。
- `.lnk` 的 `LauncherItem.Path` 必须保持快捷方式路径，启动继续使用 `UseShellExecute = true`。
- 图标缓存目录固定为 `%LOCALAPPDATA%\FloatDesk\launcher-icons`，图标二进制不写入导出的设置 JSON。
- Shell 解析、图标提取和 PNG 解码不得阻塞 WPF UI 线程。
- 图标失败、快捷方式损坏或缓存损坏时必须回退到现有占位图标，不能阻止添加、保存或启动。
- 关闭应用坞只隐藏 UI，不得删除快捷应用或修改 `ShowInWheel`。
- 不使用 `git add -A`；每个提交只暂存该任务明确列出的文件。

## Review Focus

- 带空格、中文、自定义参数和工作目录的 `.lnk` 必须以原快捷方式启动，不能因解析目标而丢失语义；由 Task 2 的 `LauncherEntryResolverPreservesShortcutPath` 和现有启动器测试覆盖。
- 同一路径以不同大小写或同批次重复拖入时不能产生重复条目；由 Task 2 的 `DeduplicatesNormalizedPathsAndBatchEntries` 覆盖。
- 目标缺失或损坏的 `.lnk` 不能让拖放挂起或崩溃，且必须给出失败反馈；由 Task 2 的 `BrokenShortcutReturnsFailedResultWithoutAdding` 覆盖。
- 图标提取器失败、PNG 损坏或缓存文件缺失时，快捷应用仍应显示占位图并可编辑/启动；由 Task 3 的缓存回退测试和 Task 4 的呈现器回退测试覆盖。
- 应用坞开关与其他设置并发修改时不能被旧设置快照覆盖；由 Task 1 的合并测试覆盖。

---

### Task 1: 应用坞显示设置与轮盘可见性

**Files:**

- Modify: `src/FloatDesk.Core/Models/AppSettings.cs`
- Modify: `src/FloatDesk.App/ViewModels/SettingsViewModel.cs`
- Modify: `src/FloatDesk.App/Views/SettingsWindow.xaml`
- Modify: `src/FloatDesk.App/Views/QuickPanelWindow.xaml.cs`
- Test: `tests/FloatDesk.Infrastructure.Tests/SettingsStoreTests.cs`
- Test: `tests/FloatDesk.Ui.Tests/SettingsViewModelMergeTests.cs`
- Test: `tests/FloatDesk.Ui.Tests/SettingsWindowVisualTests.cs`
- Test: `tests/FloatDesk.Ui.Tests/QuickPanelWindowLayoutTests.cs`
- Docs: `docs/superpowers/specs/2026-10-27-launcher-icon-import-dock-visibility-design.md`
- Docs: `docs/superpowers/plans/2026-10-27-launcher-icon-import-dock-visibility.md`

**Interfaces:**

- Consumes: existing `AppSettings`, `SettingsViewModel.BuildCandidate`, `QuickPanelWindow.Apply`.
- Produces: `AppSettings.ShowLauncherDock { get; init; } = true`.
- Produces: `SettingsViewModel.ShowLauncherDock { get; set; }`, default `true`.
- Produces: `QuickPanelWindow.Apply` shows `LauncherDock` only when `settings.ShowLauncherDock && dockLaunchers.Length > 0`.

- [ ] **Step 1: Write failing setting, UI, and wheel visibility tests**

Add these assertions to the existing test classes:

```csharp
[Fact]
public void LauncherDockVisibilityDefaultsToTrue() =>
    Assert.True(AppSettings.CreateDefault().ShowLauncherDock);

[Fact]
public void EditedLauncherDockVisibilityMergesIntoLatestVersion()
{
    var original = AppSettings.CreateDefault();
    var viewModel = SettingsViewModel.From(original);
    viewModel.ShowLauncherDock = false;
    var latest = original with { BrowserTopmost = true };

    var candidate = viewModel.BuildCandidate(latest);

    Assert.False(candidate.ShowLauncherDock);
    Assert.True(candidate.BrowserTopmost);
}

[Fact]
public void UntouchedLauncherDockVisibilityPreservesConcurrentChange()
{
    var original = AppSettings.CreateDefault();
    var latest = original with { ShowLauncherDock = false };

    var candidate = SettingsViewModel.From(original).BuildCandidate(latest);

    Assert.False(candidate.ShowLauncherDock);
}
```

Add a settings migration test that removes `showLauncherDock` from JSON and asserts the result is `true`, plus a save/load round-trip that preserves `false`. In `SettingsWindowVisualTests`, assert `ShowLauncherDockCheckBox` binds `{Binding ShowLauncherDock}` and has `AutomationProperties.Name`. In `QuickPanelWindowLayoutTests`, apply settings with `ShowLauncherDock = true` and `false`, assert `LauncherDock.Visibility` is `Visible` and `Collapsed`, and confirm wheel launcher buttons remain.

- [ ] **Step 2: Run focused tests and verify failure**

Run:

```powershell
dotnet test tests/FloatDesk.Infrastructure.Tests/FloatDesk.Infrastructure.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~LauncherDock"
dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~LauncherDock"
```

Expected: compilation or test failure because `ShowLauncherDock` and `ShowLauncherDockCheckBox` do not exist.

- [ ] **Step 3: Implement the setting, ViewModel merge, and settings switch**

Add to the `AppSettings` init-property section:

```csharp
public bool ShowLauncherDock { get; init; } = true;
```

Add `[ObservableProperty] private bool _showLauncherDock = true;` to `SettingsViewModel`. Initialize it from `settings.ShowLauncherDock` in `From`. In `BuildCandidate`, override `latest.ShowLauncherDock` only when the ViewModel value differs from `_baseline.ShowLauncherDock`.

Add this control to the existing “快捷应用共用同一个轮盘” card in the Wheel settings tab:

```xml
<CheckBox x:Name="ShowLauncherDockCheckBox"
          Content="显示轮盘下方快捷应用栏"
          IsChecked="{Binding ShowLauncherDock}"
          Style="{StaticResource ToggleSwitchStyle}"
          AutomationProperties.Name="显示轮盘下方快捷应用栏" />
```

Change `QuickPanelWindow.Apply` visibility to:

```csharp
LauncherDock.Visibility =
    settings.ShowLauncherDock && dockLaunchers.Length > 0
        ? Visibility.Visible
        : Visibility.Collapsed;
```

- [ ] **Step 4: Run focused tests and verify success**

Run:

```powershell
dotnet test tests/FloatDesk.Infrastructure.Tests/FloatDesk.Infrastructure.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~LauncherDock"
dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~LauncherDock"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add docs/superpowers/specs/2026-10-27-launcher-icon-import-dock-visibility-design.md docs/superpowers/plans/2026-10-27-launcher-icon-import-dock-visibility.md src/FloatDesk.Core/Models/AppSettings.cs src/FloatDesk.App/ViewModels/SettingsViewModel.cs src/FloatDesk.App/Views/SettingsWindow.xaml src/FloatDesk.App/Views/QuickPanelWindow.xaml.cs tests/FloatDesk.Infrastructure.Tests/SettingsStoreTests.cs tests/FloatDesk.Ui.Tests/SettingsViewModelMergeTests.cs tests/FloatDesk.Ui.Tests/SettingsWindowVisualTests.cs tests/FloatDesk.Ui.Tests/QuickPanelWindowLayoutTests.cs
git commit -m "feat: add launcher dock visibility setting"
```

---

### Task 2: 拖放导入与快捷方式解析

**Files:**

- Create: `src/FloatDesk.Core/Launcher/LauncherImportModels.cs`
- Create: `src/FloatDesk.Core/Launcher/LauncherDropParser.cs`
- Create: `src/FloatDesk.Windows/Launcher/LauncherEntryResolver.cs`
- Modify: `src/FloatDesk.App/Views/SettingsWindow.xaml`
- Modify: `src/FloatDesk.App/Views/SettingsWindow.xaml.cs`
- Test: `tests/FloatDesk.Core.Tests/LauncherDropParserTests.cs`
- Test: `tests/FloatDesk.Windows.Tests/LauncherEntryResolverTests.cs`
- Test: `tests/FloatDesk.Ui.Tests/SettingsWindowVisualTests.cs`

**Interfaces:**

- Consumes: `LauncherItem`, `LauncherKind`.
- Produces: `LauncherImportResolution(string Name, LauncherKind Kind, string Path, string? WorkingDirectory)`.
- Produces: `LauncherDropResult(IReadOnlyList<LauncherItem> Added, IReadOnlyList<string> Duplicates, IReadOnlyList<string> Unsupported, IReadOnlyList<string> Failed)` with a Chinese `Summary` property.
- Produces: `ILauncherEntryResolver.Resolve(string fullPath) -> LauncherImportResolution?`.
- Produces: `LauncherDropParser.Parse(IEnumerable<string> droppedPaths, IReadOnlyList<LauncherItem> existing, ILauncherEntryResolver resolver, Func<Guid>? createId = null) -> LauncherDropResult`.
- Produces: `LauncherEntryResolver : ILauncherEntryResolver`, supporting `.exe` and valid `.lnk`.

- [ ] **Step 1: Write failing drop classification and shortcut resolution tests**

Core tests must include:

```csharp
[Fact]
public void AddsExeAndShortcutAndKeepsSequentialOrder()
{
    using var temp = new TemporaryDirectory();
    var exe = temp.CreateFile("编辑器.EXE");
    var lnk = temp.CreateFile("浏览器.LNK");
    var resolver = new FakeResolver(
        path => new LauncherImportResolution(
            Path.GetFileNameWithoutExtension(path),
            Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                ? LauncherKind.Exe
                : LauncherKind.Shortcut,
            path,
            Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(path)
                : null));

    var result = LauncherDropParser.Parse(
        [exe, lnk],
        existing: [],
        resolver,
        createId: NewSequentialGuid());

    Assert.Equal(["编辑器", "浏览器"], result.Added.Select(item => item.Name));
    Assert.Equal([0, 1], result.Added.Select(item => item.Order));
    Assert.All(result.Added, item => Assert.False(item.ShowInWheel));
}

[Fact]
public void DeduplicatesNormalizedPathsAndBatchEntries()
{
    using var temp = new TemporaryDirectory();
    var existingPath = temp.CreateFile("a.exe");
    var addedPath = temp.CreateFile("b.exe");
    var existing = new LauncherItem(
        Guid.NewGuid(), "A", LauncherKind.Exe, existingPath,
        [], null, 0, ShowInWheel: true);
    var resolver = new FakeResolver(path =>
        new LauncherImportResolution(
            Path.GetFileNameWithoutExtension(path),
            LauncherKind.Exe,
            path,
            Path.GetDirectoryName(path)));

    var result = LauncherDropParser.Parse(
        [existingPath.ToUpperInvariant(), addedPath, addedPath.ToUpperInvariant()],
        [existing],
        resolver,
        createId: () => Guid.NewGuid());

    Assert.Single(result.Added);
    Assert.Equal(2, result.Duplicates.Count);
}
```

Add a parser test where no resolver caches are delegated to, and assert an `.exe` resolution preserves the executable directory. `LauncherEntryResolverTests` creates a real temporary `.exe` and uses a COM test helper to create a real `.lnk`, then asserts:

```csharp
[Fact]
public void LauncherEntryResolverPreservesShortcutPath()
{
    var resolved = resolver.Resolve(shortcutPath);

    Assert.NotNull(resolved);
    Assert.Equal(LauncherKind.Shortcut, resolved.Kind);
    Assert.Equal(shortcutPath, resolved.Path);
    Assert.Null(resolved.WorkingDirectory);
}
```

Name the test `LauncherEntryResolverPreservesShortcutPath`. Add this parser test for a resolver that rejects a broken shortcut:

```csharp
[Fact]
public void BrokenShortcutReturnsFailedResultWithoutAdding()
{
    using var temp = new TemporaryDirectory();
    var shortcut = temp.CreateFile("broken.lnk");

    var result = LauncherDropParser.Parse(
        [shortcut],
        existing: [],
        new FakeResolver(_ => null));

    Assert.Empty(result.Added);
    Assert.Single(result.Failed);
}
```

`SettingsWindowVisualTests` must assert `LauncherDropSurface` exists, has `AllowDrop="True"`, has `DragOver`, `DragLeave`, and `Drop` handlers, and contains drag hint text.

- [ ] **Step 2: Run focused tests and verify failure**

Run:

```powershell
dotnet test tests/FloatDesk.Core.Tests/FloatDesk.Core.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~LauncherDropParserTests"
dotnet test tests/FloatDesk.Windows.Tests/FloatDesk.Windows.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~LauncherEntryResolverTests"
dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~LauncherDrop"
```

Expected: FAIL because the parser, resolver, and drop UI do not exist.

- [ ] **Step 3: Implement the Core parser and Windows shortcut resolver**

`LauncherDropParser.Parse` must:

1. Ignore null or whitespace paths.
2. Normalize with `Path.GetFullPath`.
3. Accept only existing `.exe` and `.lnk` files, case-insensitively.
4. Deduplicate against existing launchers and the current batch, case-insensitively.
5. Call `ILauncherEntryResolver.Resolve`; add the path to `Failed` when it returns `null`.
6. Append items after the current maximum `Order`, with `ShowInWheel = false` and empty arguments.

`LauncherEntryResolver` resolves `.exe` with the file stem, executable directory, and `LauncherKind.Exe`. For `.lnk`, use `IShellLinkW` read-only to obtain and validate the target, but return the original `.lnk` path and `WorkingDirectory = null`.

- [ ] **Step 4: Implement the settings drag-and-drop interaction**

Name the Quick Apps page root `LauncherDropSurface`, set `AllowDrop="True"`, and handle `DragOver`, `DragLeave`, and `Drop`. Show a highlight and hint while valid file data is dragged over the surface.

`SettingsWindow` creates a `LauncherEntryResolver`. Copy `DataFormats.FileDrop` paths before calling `Task.Run` with `LauncherDropParser.Parse`; append `result.Added` to `_viewModel.Launchers` back on the UI thread and set `SaveStatusText.Text = result.Summary`. Ignore a second drop while parsing is in progress.

- [ ] **Step 5: Run focused tests and verify success**

Run:

```powershell
dotnet test tests/FloatDesk.Core.Tests/FloatDesk.Core.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~LauncherDropParserTests"
dotnet test tests/FloatDesk.Windows.Tests/FloatDesk.Windows.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~LauncherEntryResolverTests"
dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~LauncherDrop"
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/FloatDesk.Core/Launcher/LauncherImportModels.cs src/FloatDesk.Core/Launcher/LauncherDropParser.cs src/FloatDesk.Windows/Launcher/LauncherEntryResolver.cs src/FloatDesk.App/Views/SettingsWindow.xaml src/FloatDesk.App/Views/SettingsWindow.xaml.cs tests/FloatDesk.Core.Tests/LauncherDropParserTests.cs tests/FloatDesk.Windows.Tests/LauncherEntryResolverTests.cs tests/FloatDesk.Ui.Tests/SettingsWindowVisualTests.cs
git commit -m "feat: import launchers with drag and drop"
```

---

### Task 3: Shell 图标提取与本地缓存

**Files:**

- Modify: `src/FloatDesk.App/Bootstrap/AppDataPaths.cs`
- Create: `src/FloatDesk.App/Launcher/ILauncherIconProvider.cs`
- Create: `src/FloatDesk.App/Launcher/LauncherIconCache.cs`
- Create: `src/FloatDesk.Windows/Launcher/LauncherIconExtractor.cs`
- Test: `tests/FloatDesk.Ui.Tests/LauncherIconCacheTests.cs`
- Test: `tests/FloatDesk.Windows.Tests/LauncherIconExtractorTests.cs`

**Interfaces:**

- Consumes: `LauncherItem.Kind/Path`.
- Produces: `AppDataPaths.LauncherIcons`, set to `Path.Combine(Root, "launcher-icons")` and created by `EnsureCreated`.
- Produces: `ILauncherIconProvider.GetAsync(LauncherItem item, CancellationToken cancellationToken) -> Task<ImageSource?>`.
- Produces: `LauncherIconCache(string cacheDirectory, Func<string, CancellationToken, Task<byte[]?>> extractPngAsync)` implementing `ILauncherIconProvider`.
- Produces: `LauncherIconCache.Preload(IEnumerable<LauncherItem> items)`, `LauncherIconCache.Prune(IEnumerable<LauncherItem> items)`, and `LauncherIconCache.CreateCacheKey(LauncherItem item)`.
- Produces: `LauncherIconExtractor.ExtractPngAsync(string path, CancellationToken cancellationToken) -> Task<byte[]?>`.

- [ ] **Step 1: Write failing icon cache and extractor tests**

Use a temporary directory and a counting fake extractor:

```csharp
[Fact]
public async Task ReusesCachedPngWithoutCallingExtractorAgain()
{
    using var temp = new TemporaryDirectory();
    var calls = 0;
    var cache = new LauncherIconCache(
        temp.Path,
        (_, _) =>
        {
            calls++;
            return Task.FromResult<byte[]?>(OnePixelPng);
        });
    var item = NewLauncher(temp.CreateFile("app.exe"));

    var first = await cache.GetAsync(item, CancellationToken.None);
    var second = await cache.GetAsync(item, CancellationToken.None);

    Assert.NotNull(first);
    Assert.NotNull(second);
    Assert.Equal(1, calls);
}

[Fact]
public async Task CorruptCacheFallsBackToFreshExtraction()
{
    using var temp = new TemporaryDirectory();
    var item = NewLauncher(temp.CreateFile("app.exe"));
    var cache = new LauncherIconCache(
        temp.Path,
        (_, _) => Task.FromResult<byte[]?>(OnePixelPng));
    var key = LauncherIconCache.CreateCacheKey(item);
    await File.WriteAllBytesAsync(
        Path.Combine(temp.Path, $"{key}.png"),
        [0x00]);

    var image = await cache.GetAsync(item, CancellationToken.None);

    Assert.NotNull(image);
}
```

Add tests for: extractor returning `null` produces `null`; `Prune` removes orphan PNG and temporary files; cache key changes when the launcher path or file write timestamp changes; concurrent calls for one key invoke the extractor once; a `LauncherKind.Folder` item hashes directory write time instead of file length and still returns an icon from the fake extractor.

Windows test:

```csharp
[Fact]
public async Task ExtractsPngFromSystemExecutable()
{
    using var extractor = new LauncherIconExtractor();
    var png = await extractor.ExtractPngAsync(
        Path.Combine(Environment.SystemDirectory, "cmd.exe"),
        CancellationToken.None);

    Assert.NotNull(png);
    Assert.Equal(
        new byte[] { 0x89, 0x50, 0x4E, 0x47 },
        png.Take(4));
}
```

- [ ] **Step 2: Run focused tests and verify failure**

Run:

```powershell
dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~LauncherIconCacheTests"
dotnet test tests/FloatDesk.Windows.Tests/FloatDesk.Windows.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~LauncherIconExtractorTests"
```

Expected: FAIL because the icon interface, cache, and extractor do not exist.

- [ ] **Step 3: Implement AppDataPaths, the Windows extractor, and cache**

`AppDataPaths.EnsureCreated` must create `LauncherIcons`.

`LauncherIconExtractor` processes requests on one background STA worker. Prefer `IShellItemImageFactory` at 256 px, fall back to `SHGetFileInfo`, then encode the icon to a transparent PNG. Destroy every HICON, HBITMAP, and HGLOBAL on all paths.

`LauncherIconCache` hashes the normalized launcher path, kind, target length, and last-write ticks with SHA-256 for the cache filename. Files use `FileInfo.Length`; folders and unavailable paths use length `0` and `Directory.GetLastWriteTimeUtc` where available, and key creation must never throw for a missing path. On a hit, decode to a frozen WPF `BitmapImage`; if decoding fails, delete the file and re-extract. On a miss, call `extractPngAsync`, write to `*.tmp`, then atomically move to `*.png`. Coalesce concurrent requests by key. `Prune` deletes only unreferenced `.png` and `.tmp` files in its cache directory.

- [ ] **Step 4: Run focused tests and verify success**

Run:

```powershell
dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~LauncherIconCacheTests"
dotnet test tests/FloatDesk.Windows.Tests/FloatDesk.Windows.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~LauncherIconExtractorTests"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/FloatDesk.App/Bootstrap/AppDataPaths.cs src/FloatDesk.App/Launcher/ILauncherIconProvider.cs src/FloatDesk.App/Launcher/LauncherIconCache.cs src/FloatDesk.Windows/Launcher/LauncherIconExtractor.cs tests/FloatDesk.Ui.Tests/LauncherIconCacheTests.cs tests/FloatDesk.Windows.Tests/LauncherIconExtractorTests.cs
git commit -m "feat: cache shell launcher icons"
```

---

### Task 4: 图标呈现与运行时集成

**Files:**

- Create: `src/FloatDesk.App/Views/LauncherIconView.cs`
- Modify: `src/FloatDesk.App/ViewModels/SettingsViewModel.cs`
- Modify: `src/FloatDesk.App/Views/SettingsWindow.xaml`
- Modify: `src/FloatDesk.App/Views/SettingsWindow.xaml.cs`
- Modify: `src/FloatDesk.App/Views/QuickPanelWindow.xaml.cs`
- Modify: `src/FloatDesk.App/AppRuntime.cs`
- Test: `tests/FloatDesk.Ui.Tests/LauncherIconViewTests.cs`
- Test: `tests/FloatDesk.Ui.Tests/SettingsWindowVisualTests.cs`
- Test: `tests/FloatDesk.Ui.Tests/QuickPanelWindowLayoutTests.cs`

**Interfaces:**

- Consumes: `ILauncherIconProvider.GetAsync(LauncherItem, CancellationToken)`, `LauncherIconCache`, `LauncherIconExtractor`, `AppDataPaths.LauncherIcons`.
- Produces: `LauncherIconView : Grid` with dependency properties `LauncherItem? Launcher`, `ILauncherIconProvider? IconProvider`, and `double IconSize`; test-visible members `bool IsFallbackVisible` and `ImageSource? DisplayedImage` expose its current visual state.
- Produces: `SettingsViewModel.LauncherIconProvider { get; set; }`.
- Produces: `QuickPanelWindow(ILauncherIconProvider? launcherIcons)` plus the existing parameterless constructor.

- [ ] **Step 1: Write failing icon presenter and integration tests**

`LauncherIconViewTests` uses a controllable provider:

```csharp
[Fact]
public async Task ShowsFallbackUntilImageArrivesThenDisplaysImage()
{
    var completion = new TaskCompletionSource<ImageSource?>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var provider = new FakeIconProvider(
        (_, _) => completion.Task);
    var item = NewLauncher();
    var view = new LauncherIconView
    {
        Launcher = item,
        IconProvider = provider,
        IconSize = 42
    };

    Assert.True(view.IsFallbackVisible);

    completion.SetResult(OnePixelImage());
    await provider.WaitForLastRequestAsync();

    Assert.False(view.IsFallbackVisible);
    Assert.NotNull(view.DisplayedImage);
}

[Fact]
public async Task ProviderFailureKeepsFallbackVisible()
{
    var provider = new FakeIconProvider(
        (_, _) => Task.FromResult<ImageSource?>(null));
    var view = new LauncherIconView
    {
        Launcher = NewLauncher(),
        IconProvider = provider,
        IconSize = 24
    };

    await provider.WaitForLastRequestAsync();

    Assert.True(view.IsFallbackVisible);
}
```

Add a stale-request test: change `Launcher` while the first provider request is pending, complete the first request, then complete the second; only the second image may be displayed.

In `SettingsWindowVisualTests`, assert the Quick Apps grid contains a template column with `LauncherIconView` bound to the selected launcher and `IconProvider`. In `QuickPanelWindowLayoutTests`, apply one wheel launcher and one dock launcher, find descendant `LauncherIconView` controls, and assert sizes 42 and 24 are present.

- [ ] **Step 2: Run focused tests and verify failure**

Run:

```powershell
dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~LauncherIconViewTests"
dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~LauncherIcon"
```

Expected: FAIL because `LauncherIconView` and runtime icon integration do not exist.

- [ ] **Step 3: Implement `LauncherIconView`**

`LauncherIconView` is a small `Grid` with a colored circular fallback and an `Image`. Its dependency properties are:

```csharp
public LauncherItem? Launcher { get; set; }
public ILauncherIconProvider? IconProvider { get; set; }
public double IconSize { get; set; }
```

On `Loaded` or when either icon input changes, cancel the previous request, reset to the fallback, and call `IconProvider.GetAsync`. Only the current generation may replace the visual. A non-null frozen image hides the fallback; `null`, cancellation, or exception leaves it visible. Keep the control UI-thread-affine and use `BitmapScalingMode.HighQuality`.

- [ ] **Step 4: Integrate icons into settings, wheel, dock, and runtime**

Add `SettingsViewModel.LauncherIconProvider` as a non-serialized nullable property. Assign the provider after constructing or reloading the ViewModel in `SettingsWindow`.

In `SettingsWindow.xaml`, add the local namespace and a 44 px `DataGridTemplateColumn` with:

```xml
<views:LauncherIconView
    Width="26"
    Height="26"
    IconSize="26"
    Launcher="{Binding}"
    IconProvider="{Binding DataContext.LauncherIconProvider,
                           RelativeSource={RelativeSource AncestorType=DataGrid}}" />
```

In `QuickPanelWindow`, add the provider constructor overload and use `LauncherIconView` inside `CreateWheelLauncherContent` at 42 DIP and `CreateLauncherContent(LauncherItem item)` at 24 DIP. The fallback must remain visually equivalent to the existing blue circular glyph.

In `AppRuntime`, construct one `LauncherIconExtractor` and one `LauncherIconCache(paths.LauncherIcons, extractor.ExtractPngAsync)`. Pass the cache to `QuickPanelWindow` and `SettingsWindow`; call `Preload(settings.Launchers)` and `Prune(settings.Launchers)` from `ApplySettings`; dispose both in runtime disposal.

- [ ] **Step 5: Run focused tests and verify success**

Run:

```powershell
dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~LauncherIconViewTests"
dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~LauncherIcon"
```

Expected: PASS.

- [ ] **Step 6: Run the UI suite**

Run:

```powershell
dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Release --no-restore -p:NuGetAudit=false
```

Expected: all UI tests pass.

- [ ] **Step 7: Commit**

```powershell
git add src/FloatDesk.App/Views/LauncherIconView.cs src/FloatDesk.App/ViewModels/SettingsViewModel.cs src/FloatDesk.App/Views/SettingsWindow.xaml src/FloatDesk.App/Views/SettingsWindow.xaml.cs src/FloatDesk.App/Views/QuickPanelWindow.xaml.cs src/FloatDesk.App/AppRuntime.cs tests/FloatDesk.Ui.Tests/LauncherIconViewTests.cs tests/FloatDesk.Ui.Tests/SettingsWindowVisualTests.cs tests/FloatDesk.Ui.Tests/QuickPanelWindowLayoutTests.cs
git commit -m "feat: show real launcher icons"
```

---

## Final Verification

- [ ] **Step 1: Run the full Release test suite**

Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/test.ps1 -Configuration Release
```

Expected: every test project passes. If the known sandbox-only GDI capture test fails with an invalid handle, rerun that test outside the sandbox before treating it as a product failure.

- [ ] **Step 2: Build Release with warnings as errors**

Run:

```powershell
dotnet build FloatDesk.sln -c Release --no-restore -p:NuGetAudit=false
```

Expected: `0 Warning(s)` and `0 Error(s)`.

- [ ] **Step 3: Check the worktree and diff**

Run:

```powershell
git status --short
git diff --check
```

Expected: only task-owned files are modified; no whitespace errors. Preserve any unrelated user changes exactly as found.

- [ ] **Step 4: Manual verification checklist**

On Windows 10 22H2 x64 and Windows 11 x64:

1. Drag a normal `.exe`, a Chinese-named `.exe`, a normal `.lnk`, and a shortcut with arguments into Quick Apps.
2. Confirm duplicate paths and unsupported files are reported without changing existing rows.
3. Confirm icons appear asynchronously in Settings, the wheel, and the dock without freezing the panel.
4. Delete or corrupt one cached PNG and confirm fallback then background recovery.
5. Turn off “显示轮盘下方快捷应用栏”, apply, and reopen the panel; confirm only the dock disappears.
6. Turn it back on and confirm the dock returns without restarting.
7. Check 100%, 125%, 150%, mixed-DPI, light, dark, and high-contrast displays.

Expected: all checks pass. Report any Windows 11 or mixed-DPI item that could not be hands-on verified.
