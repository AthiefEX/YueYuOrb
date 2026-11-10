using System.Windows.Threading;
using System.Threading;
using System.IO;
using FloatDesk.App;
using FloatDesk.App.Views;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;

namespace SettingsWindowProbe;

internal static partial class Program
{
    private static bool _cleanShellProbe;
    private static bool _captureNetworkUi;
    private static string? _orbConflictExecutable;
    private static string? _orbStartupGuard;
    private static int _observeSeconds;
    [STAThread]
    private static int Main(string[] arguments)
    {
        try
        {
            if (arguments.Contains("--steam-upstream-analysis", StringComparer.Ordinal))
            {
                SteamUpstreamAnalysis.RunAsync().GetAwaiter().GetResult();
                return 0;
            }
            var bridgeIndex = Array.IndexOf(arguments, "--steam-bridge-probe");
            if (bridgeIndex >= 0)
            {
                var host = bridgeIndex + 1 < arguments.Length ? arguments[bridgeIndex + 1] : "steamcommunity.com";
                var useNoSni = !arguments.Contains("--no-sni-off", StringComparer.Ordinal);
                var repeatIndex = Array.IndexOf(arguments, "--repeat");
                var repeat = repeatIndex >= 0 && repeatIndex + 1 < arguments.Length && int.TryParse(arguments[repeatIndex + 1], out var parsed) ? parsed : 1;
                // A stable directory lets consecutive probe runs reuse the remembered edge, which
                // is what the warm-up and cross-restart hint are supposed to provide.
                var data = Path.Combine(Environment.CurrentDirectory, "artifacts", "steam-probe-data");
                Directory.CreateDirectory(data);
                SteamBridgeProbe.RunAsync(host, useNoSni, data, repeat).GetAwaiter().GetResult();
                return 0;
            }
            if (arguments.Contains("--steam-network-lease", StringComparer.Ordinal))
            {
                SteamNetworkProbe.VerifyNativeLease();
                return 0;
            }
            if (arguments.Contains("--steam-network-diagnostics", StringComparer.Ordinal))
            {
                SteamNetworkProbe.RunAsync().GetAwaiter().GetResult();
                return 0;
            }
            _cleanShellProbe = arguments.Contains("--clean-shell-probe", StringComparer.Ordinal);
            _captureNetworkUi = arguments.Contains("--steam-network-ui", StringComparer.Ordinal);
            var conflictIndex = Array.IndexOf(arguments, "--orb-conflict-executable");
            if (conflictIndex >= 0)
                _orbConflictExecutable = arguments[conflictIndex + 1];
            var guardIndex = Array.IndexOf(arguments, "--orb-startup-guard");
            if (guardIndex >= 0)
                _orbStartupGuard = arguments[guardIndex + 1];
            var observeIndex = Array.IndexOf(arguments, "--observe-seconds");
            if (observeIndex >= 0)
                _observeSeconds = Math.Clamp(int.Parse(arguments[observeIndex + 1], System.Globalization.CultureInfo.InvariantCulture), 0, 180);
            var app = new ProbeApplication
            {
                ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown
            };
            var source = new DirectoryInfo(AppContext.BaseDirectory);
            while (source is not null && !File.Exists(Path.Combine(source.FullName, "src", "FloatDesk.App", "App.xaml")))
                source = source.Parent;
            if (source is null)
                throw new InvalidOperationException("Application resources source not found.");
            var root = System.Xml.Linq.XDocument.Load(Path.Combine(source.FullName, "src", "FloatDesk.App", "App.xaml")).Root!;
            var resources = new System.Xml.Linq.XElement(root.Name.Namespace + "ResourceDictionary",
                root.Attributes().Where(attribute => attribute.IsNamespaceDeclaration),
                root.Element(root.Name.Namespace + "Application.Resources")!.Nodes());
            app.Resources = (System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Parse(resources.ToString());
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(
                    Dispatcher.CurrentDispatcher));
            var consoleIndex = Array.IndexOf(arguments, "--hyperv-console-ui");
            if (arguments.Contains("--assistant-ui", StringComparer.Ordinal))
            {
                var task = VerifyAssistantUiAsync();
                while (!task.IsCompleted) Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                task.GetAwaiter().GetResult(); app.Shutdown(); return 0;
            }
            if (arguments.Contains("--floating-browser-background-ui", StringComparer.Ordinal))
            {
                var task = VerifyFloatingBrowserBackgroundAsync();
                while (!task.IsCompleted) Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                task.GetAwaiter().GetResult(); app.Shutdown(); return 0;
            }
            if(arguments.Contains("--glass-frame-ui",StringComparer.Ordinal))
            {GlassFrameProbe.Run();app.Shutdown();return 0;}
            if (arguments.Contains("--console-power-ui", StringComparer.Ordinal))
            {
                var task = VerifyConsolePowerUiAsync();
                while (!task.IsCompleted) Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                task.GetAwaiter().GetResult(); app.Shutdown(); return 0;
            }
            if (arguments.Contains("--console-close-policy-ui", StringComparer.Ordinal))
            {
                var task = VerifyConsoleClosePolicyAsync();
                while (!task.IsCompleted) Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                task.GetAwaiter().GetResult(); app.Shutdown(); return 0;
            }
            if (arguments.Contains("--vm-state-indicator-ui", StringComparer.Ordinal))
            {
                var task = VerifyVmStateIndicatorAsync();
                while (!task.IsCompleted) Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                task.GetAwaiter().GetResult(); app.Shutdown(); return 0;
            }
            if (arguments.Contains("--console-startup-failure-ui", StringComparer.Ordinal))
            {
                var task = VerifyConsoleStartupFailureUiAsync();
                while (!task.IsCompleted)
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                task.GetAwaiter().GetResult();
                app.Shutdown();
                return 0;
            }
            if (consoleIndex >= 0)
            {
                var task = VerifyHyperVConsoleUiAsync(Guid.Parse(arguments[consoleIndex + 1]));
                while (!task.IsCompleted)
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                task.GetAwaiter().GetResult();
                app.Shutdown();
                return 0;
            }
            if (arguments.Contains("--scroll-watch", StringComparer.Ordinal))
            {
                WatchScrolling();
                app.Shutdown();
                return 0;
            }
            if (arguments.Contains("--local-session", StringComparer.Ordinal))
            {
                var task = VerifyLocalSessionCyclesAsync(arguments.Contains("--repeat", StringComparer.Ordinal) ? 2 : 1);
                while (!task.IsCompleted)
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                task.GetAwaiter().GetResult();
                app.Shutdown();
                return 0;
            }
            if (arguments.Contains("--broker-check", StringComparer.Ordinal))
            {
                var native = new FloatDesk.Windows.DesktopSessions.ChildSessionNative();
                if (!native.TryGetChildSessionId(out var childId) || childId is 0 or uint.MaxValue)
                    throw new InvalidOperationException("No child association to probe.");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var task = new FloatDesk.Windows.DesktopSessions.SessionBrokerClient(timeout: TimeSpan.FromSeconds(15)).LaunchAsync(
                    Guid.NewGuid(), Guid.NewGuid(), childId, timeout.Token);
                while (!task.IsCompleted)
                    Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var bootstrap = task.GetAwaiter().GetResult();
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(bootstrap.BootstrapSecret);
                Console.WriteLine($"Broker accepted bootstrap: session={childId} process={bootstrap.ProcessId}");
                app.Shutdown();
                return 0;
            }
            VerifyWindow(AppSettings.CreateDefault());
            VerifyWindow(CreateDesktopSessionSettings());
            VerifyWindow(CreateLargeDesktopSessionSettings());
            VerifyWindow(CreateScopedDesktopSessionSettings());
            VerifyAutomationUi();
            VerifyRdpConfiguration();
            app.Shutdown();
            Console.WriteLine("Settings window lifecycle PASS.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void VerifyWindow(AppSettings settings)
    {
        var window = new SettingsWindow(
            new FixedSettingsCoordinator(settings),
            new AppDataPaths(),
            currentAppPath: null,
            new NullCredentialStore(),
            new NullTranslationProviderFactory(),
            () => []);
        window.Show();
        window.UpdateLayout();
        window.SelectNetworkPage();
        window.UpdateLayout();
        if (_captureNetworkUi)
        {
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeService.Apply(theme);
                var frame = new DispatcherFrame();
                var renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                renderTimer.Tick += (_, _) => { renderTimer.Stop(); frame.Continue = false; };
                renderTimer.Start();
                Dispatcher.PushFrame(frame);
                window.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                var output = Path.Combine(Environment.CurrentDirectory, "artifacts", "steam-network", "screenshots");
                Directory.CreateDirectory(output);
                using var file = File.Create(Path.Combine(output, "settings-network-" + theme + ".png"));
                encoder.Save(file);
            }
        }
        Console.WriteLine("settings.steamNetwork.realWindow=PASS");
        window.SelectDesktopSessionsPage();
        window.UpdateLayout();
        var maintenanceFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var maintenanceType = typeof(SettingsWindow);
        var summary = maintenanceType.GetMethod("RefreshGuestCredentialSummary", maintenanceFlags)!;
        var sampleVm = Guid.NewGuid();
        summary.Invoke(window, [sampleVm]);
        var passwordInput = (System.Windows.Controls.PasswordBox)maintenanceType.GetField("_guestLoginPassword", maintenanceFlags)!.GetValue(window)!;
        passwordInput.Password = "synthetic-probe-only";
        maintenanceType.GetField("_vmRefreshRunning", maintenanceFlags)!.SetValue(window, true);
        summary.Invoke(window, [null]);
        summary.Invoke(window, [sampleVm]);
        if (passwordInput.SecurePassword.Length != 20) throw new InvalidOperationException("VM status polling cleared a password being entered.");
        maintenanceType.GetField("_vmRefreshRunning", maintenanceFlags)!.SetValue(window, false);
        summary.Invoke(window, [Guid.NewGuid()]);
        if (passwordInput.SecurePassword.Length != 0) throw new InvalidOperationException("Switching VM retained another VM's password.");
        Console.WriteLine("settings.guestPasswordTargetIsolation=PASS");
        T Field<T>(string name) => (T)maintenanceType.GetField(name, maintenanceFlags)!.GetValue(window)!;
        var taskTabs = Field<System.Windows.Controls.TabControl>("_desktopTaskBackendTabs");
        if (taskTabs.Items.Count != 3) throw new InvalidOperationException("Tasks require local/VM/global tabs.");
        window.SelectAutomationPage();window.UpdateLayout();
        foreach (var index in new[] { 0, 1, 0 })
        {
            taskTabs.SelectedIndex = index; window.UpdateLayout();
            var expected = index == 0 ? DesktopSessionBackendKind.LocalChild : DesktopSessionBackendKind.HyperV;
            var taskEnvironments = Field<System.Windows.Controls.ComboBox>("_desktopTaskEnvironment").Items.Cast<EnvironmentProfile>();
            if (taskEnvironments.Any(environment => environment.Backend != expected))
                throw new InvalidOperationException("Task tab leaked another backend's environment.");
            var admin = Field<System.Windows.Controls.CheckBox>("_desktopScriptAdministrator");
            if ((admin.Visibility == System.Windows.Visibility.Visible) != (index == 1))
                throw new InvalidOperationException("Administrator script option must be VM-only.");
        }
        Console.WriteLine("settings.taskBackendTabs=PASS");
        taskTabs.SelectedIndex=2;window.UpdateLayout();
        if(Field<System.Windows.Controls.ComboBox>("_desktopTaskScope").SelectedItem is not EnvironmentProfile {Id:var globalId}||globalId!=GlobalTaskTarget.Id)
            throw new InvalidOperationException("Global tasks must explicitly target the main desktop.");
        taskTabs.SelectedIndex=0;window.UpdateLayout();
        var scope = Field<System.Windows.Controls.ComboBox>("_desktopTaskScope");
        var manual = Field<System.Windows.Controls.Expander>("_desktopTaskManualPanel");
        if (!Equals(manual.Header,"手动添加") || manual.IsExpanded)
            throw new InvalidOperationException("Manual editor must start collapsed with the requested title.");
        var taskFold=Field<List<System.Windows.Controls.Expander>>("_desktopFoldedSections")[1];
        taskFold.IsExpanded=true;window.UpdateLayout();
        foreach (var index in new[] {0,1,0})
        {
            taskTabs.SelectedIndex=index;window.UpdateLayout();
            var expectedBackend=index==0?DesktopSessionBackendKind.LocalChild:DesktopSessionBackendKind.HyperV;
            if(scope.Items.Cast<EnvironmentProfile>().Any(env=>env.Backend!=expectedBackend))
                throw new InvalidOperationException("Scope dropdown includes another backend.");
            foreach(var environment in scope.Items.Cast<EnvironmentProfile>().ToArray())
            {
                scope.SelectedItem=environment;window.UpdateLayout();
                var list=Field<System.Windows.Controls.ListBox>("_desktopTaskList");
                if(list.Items.Cast<DesktopSessionTask>().Any(task=>task.EnvironmentId!=environment.Id))
                    throw new InvalidOperationException("Tasks must be isolated per selected environment.");
                if(list.Items.Count>0)
                {
                    var task=(DesktopSessionTask)list.Items[0];
                    list.ScrollIntoView(task);window.UpdateLayout();
                    window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
                    var row=(System.Windows.FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(task);
                    var toggle=Descendants(row).OfType<System.Windows.Controls.CheckBox>().Single();
                    toggle.IsChecked=!task.Enabled;
                    toggle.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    var vm=Field<FloatDesk.App.ViewModels.SettingsViewModel>("_viewModel");
                    if(vm.DesktopSessionTasks.Single(item=>item.Id==task.Id).Enabled==task.Enabled)
                        throw new InvalidOperationException("Task row switch did not update the saved draft.");
                }
            }
        }
        taskFold.IsExpanded=false;
        Console.WriteLine("settings.taskScopeAndRowSwitch=PASS");
        var taskViewModel = Field<FloatDesk.App.ViewModels.SettingsViewModel>("_viewModel");
        if(scope.SelectedItem is EnvironmentProfile currentScope &&
            taskViewModel.DesktopSessionTasks.FirstOrDefault(task=>task.EnvironmentId!=currentScope.Id) is {} foreignTask)
        {
            Field<System.Windows.Controls.TextBox>("_desktopTaskJson").Text=DesktopTaskPackagePolicy.Serialize(foreignTask with {EnvironmentId=currentScope.Id});
            maintenanceType.GetMethod("SaveDesktopTaskJson",maintenanceFlags)!.Invoke(window,[]);
            if(taskViewModel.DesktopSessionTasks.Single(task=>task.Id==foreignTask.Id).EnvironmentId!=foreignTask.EnvironmentId)
                throw new InvalidOperationException("JSON task ID collision must not move another environment's task.");
        }
        if (taskViewModel.DesktopSessionTasks.Count > 0)
        {
            var task = taskViewModel.DesktopSessionTasks[0];
            var env = taskViewModel.DesktopSessionEnvironments.Single(item => item.Id == task.EnvironmentId);
            taskTabs.SelectedIndex = env.Backend == DesktopSessionBackendKind.HyperV ? 1 : 0;
            scope.SelectedItem=scope.Items.Cast<EnvironmentProfile>().Single(item=>item.Id==env.Id);
            Field<System.Windows.Controls.ListBox>("_desktopTaskList").SelectedItem = taskViewModel.DesktopSessionTasks.Single(item => item.Id == task.Id);
            Field<System.Windows.Controls.TextBox>("_desktopTaskRules").Text = "synthetic persisted rules";
            maintenanceType.GetMethod("EditSelectedTask",maintenanceFlags)!.Invoke(window,
                [new Func<DesktopSessionTask,DesktopSessionTask>(item => item with { Rules="synthetic persisted rules",McpApprovalHash=null })]);
            maintenanceType.GetMethod("ApproveDesktopTask",maintenanceFlags)!.Invoke(window,[true]);
            var approved = taskViewModel.DesktopSessionTasks.Single(item => item.Id == task.Id);
            if (!DesktopTaskPackagePolicy.IsApproved(approved) || !approved.Enabled ||
                approved.McpEnvironmentBinding != DesktopTaskPackagePolicy.EnvironmentBinding(env))
                throw new InvalidOperationException("Actual UI approval must bind the incremented revision and target.");
            taskViewModel.UpdateDesktopSessionTask(task.Id,item => item with {Rules="changed after approval"});
            if (DesktopTaskPackagePolicy.IsApproved(taskViewModel.DesktopSessionTasks.Single(item => item.Id == task.Id)))
                throw new InvalidOperationException("Editing rules must invalidate previous approval.");
            Console.WriteLine("settings.taskRulesApprovalRevision=PASS");
        }
        window.SelectDesktopSessionsPage();window.UpdateLayout();
        var columns = Field<System.Windows.Controls.Grid>("_desktopEnvironmentColumns");
        if (columns.ColumnDefinitions.Count != 3) throw new InvalidOperationException("Environment editor must use two columns and a gutter.");
        var tabs = Field<System.Windows.Controls.TabControl>("_desktopAdvancedTabs");
        if (tabs.Items.Count != 3) throw new InvalidOperationException("VM advanced settings require three tabs.");
        if (Field<System.Windows.Controls.CheckBox>("_vmInstallAutoLogon").IsChecked != false)
            throw new InvalidOperationException("Installation auto-logon must be opt-in.");
        if (Field<System.Windows.Controls.CheckBox>("_guestAutoLogonToggle").Style is null)
            throw new InvalidOperationException("Guest auto-logon must use the existing switch style.");
        if (Field<System.Windows.Controls.PasswordBox>("_guestLoginPassword").Style is null ||
            Field<System.Windows.Controls.PasswordBox>("_vmInstallPassword").Style is null)
            throw new InvalidOperationException("Guest password fields must use the themed style.");
        var folds = Field<List<System.Windows.Controls.Expander>>("_desktopFoldedSections");
        if (folds.Count != 3 || folds.Any(item => item.IsExpanded)) throw new InvalidOperationException("Installation, tasks and diagnostics must start collapsed.");
        var backend = Field<System.Windows.Controls.ComboBox>("_desktopBackendKind");
        var originalBackend = backend.SelectedItem;
        backend.SelectedItem = DesktopSessionBackendKind.LocalChild;
        window.UpdateLayout();
        if (Field<System.Windows.Controls.StackPanel>("_desktopLocalOptions").Visibility != System.Windows.Visibility.Visible ||
            Field<System.Windows.Controls.StackPanel>("_desktopHyperVOptions").Visibility != System.Windows.Visibility.Collapsed ||
            Field<System.Windows.Controls.Button>("_desktopAdvancedButton").Visibility != System.Windows.Visibility.Collapsed)
            throw new InvalidOperationException("Local selection must show startup only.");
        backend.SelectedItem = DesktopSessionBackendKind.HyperV;
        window.UpdateLayout();
        if (Field<System.Windows.Controls.StackPanel>("_desktopLocalOptions").Visibility != System.Windows.Visibility.Collapsed ||
            Field<System.Windows.Controls.StackPanel>("_desktopHyperVOptions").Visibility != System.Windows.Visibility.Visible ||
            Field<System.Windows.Controls.CheckBox>("_desktopEnvironmentGpu").Visibility != System.Windows.Visibility.Visible)
            throw new InvalidOperationException("VM selection must hide startup.");
        Field<System.Windows.Controls.Button>("_desktopAdvancedButton").RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        if (Field<System.Windows.Controls.Border>("_desktopAdvancedPanel").Visibility != System.Windows.Visibility.Visible)
            throw new InvalidOperationException("Advanced settings did not open.");
        if (!Field<System.Windows.Controls.Primitives.Popup>("_desktopAdvancedPopup").IsOpen)
            throw new InvalidOperationException("Advanced settings must open as a popup.");
        if (Field<System.Windows.Controls.WrapPanel>("_desktopVmActionRow").Visibility != System.Windows.Visibility.Visible)
            throw new InvalidOperationException("VM power actions must appear next to advanced settings.");
        foreach (System.Windows.Controls.TabItem tab in tabs.Items) { tabs.SelectedItem = tab; window.UpdateLayout(); }
        if (Environment.GetEnvironmentVariable("YUEYU_SETTINGS_LAYOUT_SCREENSHOT") is { } preview)
        {
            tabs.SelectedIndex = 0; window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            void Capture(string path, System.Windows.FrameworkElement? surface = null)
            {
                window.UpdateLayout();
                var target = surface ?? window;
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)target.ActualWidth, (int)target.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(target);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var file = File.Create(path); encoder.Save(file);
            }
            ThemeService.Apply(AppTheme.Light); Capture(preview);
            ThemeService.Apply(AppTheme.Dark); Capture(Path.ChangeExtension(preview, ".dark.png"));
            Capture(Path.ChangeExtension(preview, ".popup.dark.png"), Field<System.Windows.Controls.Border>("_desktopAdvancedPanel"));
            tabs.SelectedIndex = 2; window.UpdateLayout();
            Capture(Path.ChangeExtension(preview, ".login.dark.png"), Field<System.Windows.Controls.Border>("_desktopAdvancedPanel"));
            Field<System.Windows.Controls.Primitives.Popup>("_desktopAdvancedPopup").IsOpen = false;
            var page = (System.Windows.FrameworkElement)window.FindName("DesktopSessionsPage");
            var scroll = (System.Windows.Controls.ScrollViewer)page.Parent;
            scroll.ScrollToBottom(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Capture(Path.ChangeExtension(preview, ".folded.dark.png"));
            window.SelectAutomationPage();window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            Capture(Path.ChangeExtension(preview,".automation.dark.png"));
            var mainTabs=(System.Windows.Controls.TabControl)window.FindName("SettingsTabControl");
            mainTabs.SelectedIndex=0;window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            Capture(Path.ChangeExtension(preview,".general.dark.png"));
            window.SelectDesktopSessionsPage();
            ThemeService.Apply(AppTheme.Light);
        }
        backend.SelectedItem = DesktopSessionBackendKind.LocalChild;
        if (Field<System.Windows.Controls.Border>("_desktopAdvancedPanel").Visibility != System.Windows.Visibility.Collapsed)
            throw new InvalidOperationException("Local selection retained VM advanced panel.");
        backend.SelectedItem = originalBackend;
        var vmList = Field<System.Windows.Controls.ListBox>("_hyperVVmList");
        var originalMachines = vmList.ItemsSource;
        var profileInput = Field<System.Windows.Controls.TextBox>("_desktopBackendProfile");
        var originalProfile = profileInput.Text;
        var previousVm = new FloatDesk.Windows.DesktopSessions.HyperVVirtualMachine(Guid.NewGuid(), "Synthetic previous VM", FloatDesk.Windows.DesktopSessions.HyperVVirtualMachineState.Off, 2, 2048, 2);
        var indicator = new System.Windows.Shapes.Ellipse { DataContext = EnvironmentProfile.CreateDefault() with
        { Backend = DesktopSessionBackendKind.HyperV, BackendProfileId = previousVm.Id.ToString() } };
        var states = Field<Dictionary<Guid, FloatDesk.Windows.DesktopSessions.HyperVVirtualMachineState>>("_desktopVmStates");
        // Feed the state through the same entry point the page uses, so the freshness stamp is part
        // of what this probe exercises.
        var remember = maintenanceType.GetMethod("RememberDesktopVmState", maintenanceFlags)!;
        foreach (var (power, expectedColor) in new[] {
            (FloatDesk.Windows.DesktopSessions.HyperVVirtualMachineState.Running, System.Windows.Media.Colors.MediumSeaGreen),
            (FloatDesk.Windows.DesktopSessions.HyperVVirtualMachineState.Saved, System.Windows.Media.Colors.DodgerBlue),
            (FloatDesk.Windows.DesktopSessions.HyperVVirtualMachineState.Off, System.Windows.Media.Colors.IndianRed) })
        {
            remember.Invoke(window, [previousVm.Id, power]);
            maintenanceType.GetMethod("UpdateEnvironmentStateIndicator", maintenanceFlags)!.Invoke(window, [indicator]);
            if (indicator.Fill is not System.Windows.Media.SolidColorBrush brush || brush.Color != expectedColor || indicator.ToolTip is null)
                throw new InvalidOperationException("VM state indicator color/tooltip mismatch.");
        }
        states.Remove(previousVm.Id);
        vmList.ItemsSource = new[] { previousVm }; vmList.SelectedItem = previousVm;
        profileInput.Text = Guid.NewGuid().ToString();
        maintenanceType.GetMethod("SynchronizeSelectedVirtualMachine", maintenanceFlags)!.Invoke(window, null);
        if (vmList.SelectedItem is not null) throw new InvalidOperationException("A missing environment VM retained the previous VM as an action target.");
        profileInput.Text = originalProfile; vmList.ItemsSource = originalMachines;
        Console.WriteLine("desktop-settings.contextual-layout=PASS");
        MeasureDesktopScrolling(window);
        window.Dispatcher.Invoke(
            () => { },
            DispatcherPriority.ApplicationIdle);
        for (var i = 0; i < 4; i++)
        {
            window.Hide();
            window.Show();
            window.UpdateLayout();
            window.Dispatcher.Invoke(
                () => { },
                DispatcherPriority.ApplicationIdle);
        }
        window.Close();
        if (window.IsVisible)
            throw new InvalidOperationException("Settings window did not close.");
    }

    private static void VerifyAutomationUi()
    {
        var client=new AutomationClientTrust("probe-client","Synthetic agent","C:\\Synthetic\\agent.exe",new string('a',64),false,DateTimeOffset.UtcNow);
        var task=new DesktopSessionTask(1,Guid.NewGuid(),"existing",GlobalTaskTarget.Id,[],new(TaskTriggerKind.Manual,null,null),1,false);
        var settings=new MutableProbeSettings(AppSettings.CreateDefault() with {DesktopSessions=new(false,[],[task]){AutomationClients=[client]}});
        var window=new SettingsWindow(settings,new AppDataPaths(),null,new NullCredentialStore(),new NullTranslationProviderFactory(),()=>[]);
        window.Show();window.SelectAutomationPage();window.UpdateLayout();
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        T Field<T>(string name)=>(T)typeof(SettingsWindow).GetField(name,flags)!.GetValue(window)!;
        var rows=Field<System.Windows.Controls.StackPanel>("_automationClientRows");
        var toggle=Descendants(rows).OfType<System.Windows.Controls.CheckBox>().Single();
        toggle.IsChecked=true;toggle.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
        if(!settings.Current.DesktopSessions.AutomationClients[0].Enabled)throw new InvalidOperationException("Agent trust switch must save without Apply.");
        Field<System.Windows.Controls.TabControl>("_desktopTaskBackendTabs").SelectedIndex=2;window.UpdateLayout();
        var name=Field<System.Windows.Controls.TextBox>("_desktopTaskName");name.Text="unsaved typed name";
        settings.UpdateAsync(current=>current with {DesktopSessions=current.DesktopSessions with {Tasks=[task,task with {Id=Guid.NewGuid(),Name="external task"}]}},CancellationToken.None).GetAwaiter().GetResult();
        window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
        if(Field<System.Windows.Controls.ListBox>("_desktopTaskList").Items.Count!=2||name.Text!="unsaved typed name")
            throw new InvalidOperationException("Live task refresh must preserve raw editor input.");
        var dialogType=typeof(SettingsWindow).Assembly.GetType("FloatDesk.App.Views.AutomationConnectionDialog")!;
        foreach(var accept in new[]{false,true})
        {
            var pending=(Task<bool>)dialogType.GetMethod("ShowAsync")!.Invoke(null,[client,window])!;
            var dialog=System.Windows.Application.Current.Windows.Cast<System.Windows.Window>().Single(item=>item.Title=="外部 agent 连接");
            dialog.UpdateLayout();
            if(!window.IsEnabled)throw new InvalidOperationException("Trust prompt must not block manual trust settings.");
            var button=Descendants(dialog).OfType<System.Windows.Controls.Button>().Single(item=>Equals(item.Content,accept?"信任并允许操作":"暂不信任"));
            button.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            if(pending.GetAwaiter().GetResult()!=accept)throw new InvalidOperationException("Trust prompt returned the wrong decision.");
        }
        window.Hide();Console.WriteLine("settings.agentTrustAndLiveRefresh=PASS");
    }

    private sealed class MutableProbeSettings(AppSettings initial):ISettingsCoordinator
    {
        public AppSettings Current{get;private set;}=initial;
        public event Action<AppSettings>? Changed;
        public Task InitializeAsync(CancellationToken token)=>Task.CompletedTask;
        public Task<OperationResult> UpdateAsync(Func<AppSettings,AppSettings> update,CancellationToken token)
        {Current=update(Current);Changed?.Invoke(Current);return Task.FromResult(OperationResult.Ok());}
    }

    private static async Task VerifyLocalSessionCyclesAsync(int count)
    {
        for (var cycle = 0; cycle < count; cycle++)
        {
            Console.WriteLine($"Local session cycle {cycle + 1}/{count}");
            await VerifyLocalSessionAsync();
        }
    }

    private static async Task VerifyLocalSessionAsync()
    {
        using var conflictLock = _orbConflictExecutable is null ? null : new FileStream(
            new AppDataPaths().LockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await FloatDesk.App.DesktopSessions.LocalSessionBrokerLifecycle.SetRunningAsync(true);
        var native = new FloatDesk.Windows.DesktopSessions.ChildSessionNative();
        var profile = CreateDesktopSessionSettings().DesktopSessions.Environments[0] with
        {
            DesktopWidth = 1024, DesktopHeight = 768
        };
        var rememberedWidth = 0;
        var rememberedHeight = 0;
        using var store = new FloatDesk.App.DesktopSessions.ChildSessionAgentBootstrapStore();
        var launcher = new FloatDesk.App.DesktopSessions.RdpChildSessionLauncher(
            native,
            controlFactory: new FloatDesk.App.DesktopSessions.RdpControlFactory(_cleanShellProbe
                ? Path.Combine(Environment.SystemDirectory, "notepad.exe") : null),
            agentBootstrapper: new FloatDesk.Windows.DesktopSessions.SessionBrokerClient(),
            bootstrapStore: store,
            saveResolution: (_, width, height) =>
            {
                rememberedWidth = width;
                rememberedHeight = height;
                return Task.CompletedTask;
            });
        await using var backend = new FloatDesk.App.DesktopSessions.ChildSessionBackend(native, launcher);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        SessionHandle? handle = null;
        try
        {
            var preparation = await backend.PrepareAsync(profile, profile.StartupPolicy, timeout.Token);
            Console.WriteLine($"Local preparation success={preparation.Succeeded} code={preparation.ErrorCode}");
            if (!preparation.Succeeded)
                throw new InvalidOperationException(preparation.ErrorCode);
            handle = await backend.StartAsync(profile, preparation, timeout.Token);
            Console.WriteLine($"Local RDP session created: session={handle.WindowsSessionId}");
            await using var transport = new FloatDesk.Windows.DesktopSessions.NamedPipeSessionTransport(store.Take);
            var handshake = await transport.ConnectAsync(handle, timeout.Token);
            Console.WriteLine($"Local authenticated Agent handshake: {handshake}");
            if (!handshake.Succeeded)
                throw new InvalidOperationException(handshake.ErrorCode);
            await transport.AcquireInputLeaseAsync(handle, TimeSpan.FromSeconds(5), timeout.Token);
            try
            {
                var entry = new SessionStartupEntry(Guid.NewGuid(), "Probe Notepad",
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe"),
                    [], null, 0, 0, true, false);
                var launch = await transport.SendAsync(handle, new SessionCommand(Guid.NewGuid(), handle,
                    SessionCommandKind.LaunchApplication, SessionStartupCommandCodec.ToParameters(entry),
                    DateTimeOffset.UtcNow.AddSeconds(10)), timeout.Token);
                Console.WriteLine($"Agent application launch: success={launch.Succeeded} code={launch.ErrorCode}");
                if (!launch.Succeeded)
                    throw new InvalidOperationException(launch.ErrorCode);
                await transport.RenewInputLeaseAsync(handle, timeout.Token);
                var waiting = transport.SendAsync(handle, new SessionCommand(Guid.NewGuid(), handle,
                    SessionCommandKind.WaitForWindow, new Dictionary<string, string> { ["processName"] = "notepad" },
                    DateTimeOffset.UtcNow.AddSeconds(20)), timeout.Token);
                while (!waiting.IsCompleted)
                {
                    await Task.WhenAny(waiting, Task.Delay(1000, timeout.Token));
                    if (!waiting.IsCompleted)
                        await transport.RenewInputLeaseAsync(handle, timeout.Token);
                }
                var wait = await waiting;
                Console.WriteLine($"Agent window lookup: success={wait.Succeeded} code={wait.ErrorCode}");
                if (!wait.Succeeded)
                    throw new InvalidOperationException(wait.ErrorCode);
                if (_cleanShellProbe)
                {
                    var names = new List<string>();
                    foreach (var process in System.Diagnostics.Process.GetProcesses())
                    {
                        using (process)
                        {
                            if (process.SessionId == handle.WindowsSessionId)
                                names.Add(process.ProcessName);
                        }
                    }
                    Console.WriteLine($"Clean-shell probe: explorer={names.Count(name => name.Equals("explorer", StringComparison.OrdinalIgnoreCase))}; clash={names.Count(name => name.Contains("clash", StringComparison.OrdinalIgnoreCase))}; orb={names.Count(name => name.Equals("YueYuOrb", StringComparison.OrdinalIgnoreCase))}");
                }
                var sessionWindow = System.Windows.Application.Current.Windows.OfType<DesktopSessionWindow>().Single();
                var initialHost = (System.Windows.Forms.Integration.WindowsFormsHost)sessionWindow.FindName("RdpHost");
                var initialViewport = (System.Windows.Forms.Panel)initialHost.Child;
                Console.WriteLine($"Initial configured desktop: {initialViewport.AutoScrollMinSize.Width}x{initialViewport.AutoScrollMinSize.Height}");
                if (initialViewport.AutoScrollMinSize.Width != 1024 || initialViewport.AutoScrollMinSize.Height != 768)
                    throw new InvalidOperationException("Configured startup resolution was ignored.");
                await sessionWindow.UpdateResolutionAsync(1600, 900);
                if (rememberedWidth != 1600 || rememberedHeight != 900)
                    throw new InvalidOperationException("Resolution persistence callback was not invoked.");
                sessionWindow.UpdateLayout();
                var fullHost = (System.Windows.Forms.Integration.WindowsFormsHost)sessionWindow.FindName("RdpHost");
                var fullViewport = (System.Windows.Forms.Panel)fullHost.Child;
                fullViewport.PerformLayout();
                Console.WriteLine($"Full viewport: horizontal={fullViewport.HorizontalScroll.Visible} vertical={fullViewport.VerticalScroll.Visible}");
                if (fullViewport.HorizontalScroll.Visible || fullViewport.VerticalScroll.Visible)
                    throw new InvalidOperationException("Full-resolution viewport must not show scrollbars.");
                sessionWindow.Width = 1050;
                sessionWindow.Height = 700;
                sessionWindow.UpdateLayout();
                var viewHost = (System.Windows.Forms.Integration.WindowsFormsHost)sessionWindow.FindName("RdpHost");
                var viewport = (System.Windows.Forms.Panel)viewHost.Child;
                viewport.PerformLayout();
                Console.WriteLine($"Viewport scrollbars: horizontal={viewport.HorizontalScroll.Visible} vertical={viewport.VerticalScroll.Visible}");
                if (!viewport.HorizontalScroll.Visible || !viewport.VerticalScroll.Visible)
                    throw new InvalidOperationException("Expected both viewport scrollbars for the smaller window.");
                viewport.AutoScrollPosition = new System.Drawing.Point(250, 150);
                Console.WriteLine($"Viewport scrolled: horizontal={-viewport.AutoScrollPosition.X} vertical={-viewport.AutoScrollPosition.Y}");
                Console.WriteLine("RDP resolution update requested: 1600x900; client window resized to 1050x700.");
                await transport.RenewInputLeaseAsync(handle, timeout.Token);
                var input = await transport.SendAsync(handle, new SessionCommand(Guid.NewGuid(), handle,
                    SessionCommandKind.TypeText, new Dictionary<string, string>
                    { ["controlType"] = "Edit", ["className"] = "Edit", ["text"] = "Local session isolation probe" },
                    DateTimeOffset.UtcNow.AddSeconds(5)), timeout.Token);
                Console.WriteLine($"Agent text input: success={input.Succeeded} code={input.ErrorCode}");
                if (!input.Succeeded)
                    throw new InvalidOperationException(input.ErrorCode);
                if (_orbConflictExecutable is not null)
                {
                    foreach (var arguments in new[] { Array.Empty<string>(), new[] { "--startup" } })
                    {
                        await transport.RenewInputLeaseAsync(handle, timeout.Token);
                        var duplicate = entry with
                        {
                            ExecutablePath = arguments.Length > 0 && _orbStartupGuard is not null ? _orbStartupGuard : _orbConflictExecutable,
                            Arguments = arguments.Length > 0 && _orbStartupGuard is not null
                                ? ["--guard-startup-app", _orbConflictExecutable, "--", "--startup"] : arguments,
                            WaitForExit = true
                        };
                        var pending = transport.SendAsync(handle, new SessionCommand(Guid.NewGuid(), handle,
                            SessionCommandKind.LaunchApplication, SessionStartupCommandCodec.ToParameters(duplicate),
                            DateTimeOffset.UtcNow.AddSeconds(8)), timeout.Token);
                        while (!pending.IsCompleted)
                        {
                            await Task.WhenAny(pending, Task.Delay(1000, timeout.Token));
                            if (!pending.IsCompleted)
                                await transport.RenewInputLeaseAsync(handle, timeout.Token);
                        }
                        var result = await pending;
                        Console.WriteLine($"Published orb duplicate: startupFlag={arguments.Length > 0} guarded={arguments.Length > 0 && _orbStartupGuard is not null} success={result.Succeeded} code={result.ErrorCode}");
                        if (!result.Succeeded)
                            throw new InvalidOperationException("Published orb did not silently exit on a cross-session data lock.");
                    }
                }
            }
            finally
            {
                await transport.ReleaseInputLeaseAsync(handle, CancellationToken.None);
            }
            if (_observeSeconds > 0)
            {
                Console.WriteLine($"Observing delayed session startup for {_observeSeconds}s: session={handle.WindowsSessionId}");
                for (var elapsed = 0; elapsed < _observeSeconds; elapsed++)
                    await Task.Delay(1000);
                Console.WriteLine("Delayed startup observation complete.");
                foreach (var process in System.Diagnostics.Process.GetProcessesByName("YueYuOrb"))
                {
                    using (process)
                    {
                        if (process.SessionId == handle.WindowsSessionId)
                            throw new InvalidOperationException($"Unexpected delayed orb in child session: PID={process.Id}; path={process.MainModule?.FileName}");
                    }
                }
                Console.WriteLine("Delayed startup check: no YueYuOrb process in owned child session.");
            }
        }
        finally
        {
            if (handle is not null)
            {
                try
                {
                    await FloatDesk.App.DesktopSessions.LocalSessionBrokerLifecycle.SetRunningAsync(false);
                }
                finally
                {
                    await backend.StopAsync(handle, SessionStopMode.LogoffOwnedSession, CancellationToken.None);
                }
                Console.WriteLine("Broker service stopped; owned probe session logged off.");
            }
            else
            {
                var exists = native.TryGetChildSessionId(out var sessionId);
                Console.WriteLine($"No owned handle; childPresent={exists} session={sessionId}; no session logoff attempted.");
            }
        }
    }

    private static void WatchScrolling()
    {
        var window = new SettingsWindow(
            new FixedSettingsCoordinator(CreateLargeDesktopSessionSettings()),
            new AppDataPaths(), null, new NullCredentialStore(),
            new NullTranslationProviderFactory(), () => []);
        window.Show();
        window.SelectDesktopSessionsPage();
        window.UpdateLayout();
        var frame = new DispatcherFrame();
        var samples = new List<double>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var previous = clock.Elapsed.TotalMilliseconds;
        var wheels = 0;
        EventHandler rendering = (_, _) =>
        {
            var current = clock.Elapsed.TotalMilliseconds;
            if (clock.Elapsed.TotalSeconds > 2)
                samples.Add(current - previous);
            previous = current;
        };
        window.PreviewMouseWheel += (_, _) => wheels++;
        System.Windows.Media.CompositionTarget.Rendering += rendering;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
        timer.Tick += (_, _) => frame.Continue = false;
        timer.Start();
        Console.WriteLine("Scroll watch: move mouse over desktop-session settings and scroll for 25 seconds.");
        Dispatcher.PushFrame(frame);
        timer.Stop();
        System.Windows.Media.CompositionTarget.Rendering -= rendering;
        window.Close();
        samples.Sort();
        Console.WriteLine($"Physical wheel events={wheels}; rendering callbacks={samples.Count}");
        if (samples.Count > 0)
            Console.WriteLine($"Rendering callback gap p95={samples[(samples.Count - 1) * 95 / 100]:F2}ms max={samples[^1]:F2}ms");
    }

    private static AppSettings CreateDesktopSessionSettings()
    {
        var environmentId = Guid.NewGuid();
        return AppSettings.CreateDefault() with
        {
            DesktopSessions = new DesktopSessionSettings(
                true,
                [
                    new EnvironmentProfile(
                        environmentId,
                        "测试环境",
                        DesktopSessionBackendKind.LocalChild,
                        "local-child",
                        SessionStartupPolicy.EnvironmentDefault,
                        new SessionLimits(),
                        [])
                ],
                [
                    new DesktopSessionTask(
                        DesktopSessionFormat.CurrentTaskFormatVersion,
                        Guid.NewGuid(),
                        "测试任务",
                        environmentId,
                        [
                            new SessionTaskStep(
                                Guid.NewGuid(),
                                SessionCommandKind.Delay,
                                new Dictionary<string, string>
                                {
                                    ["milliseconds"] = "1000"
                                },
                                TimeoutSeconds: 30,
                                RetryCount: 0)
                        ],
                        new SessionTaskTrigger(
                            TaskTriggerKind.Manual,
                            null,
                            TimeZoneInfo.Local.Id),
                        Revision: 1,
                        Enabled: true)
                ])
        };
    }

    private static AppSettings CreateScopedDesktopSessionSettings()
    {
        var original=CreateDesktopSessionSettings();
        var source=original.DesktopSessions.Environments[0];
        var environments=Enumerable.Range(0,4).Select(index=>source with
        {
            Id=Guid.NewGuid(),Name="Scope "+index,
            Backend=index<2?DesktopSessionBackendKind.LocalChild:DesktopSessionBackendKind.HyperV,
            BackendProfileId=index<2?"local-child":Guid.NewGuid().ToString()
        }).ToArray();
        return original with {DesktopSessions=original.DesktopSessions with
        {Environments=environments,Tasks=environments.Select(env=>original.DesktopSessions.Tasks[0] with {Id=Guid.NewGuid(),EnvironmentId=env.Id}).ToArray()}};
    }

    private static void MeasureDesktopScrolling(SettingsWindow window)
    {
        var page = (System.Windows.FrameworkElement)window.FindName("DesktopSessionsPage");
        var scroll = page.Parent as System.Windows.Controls.ScrollViewer ??
            throw new InvalidOperationException("Desktop sessions scroll viewer missing.");
        var elapsed = new List<double>();
        for (var index = 0; index < 120; index++)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            scroll.ScrollToVerticalOffset(index % 2 == 0 ? scroll.ScrollableHeight : 0);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            elapsed.Add(timer.Elapsed.TotalMilliseconds);
        }
        elapsed.Sort();
        Console.WriteLine($"Scroll layout samples={elapsed.Count} p95={elapsed[113]:F2}ms max={elapsed[^1]:F2}ms extent={scroll.ScrollableHeight:F0}");
        foreach (var list in Descendants(page).OfType<System.Windows.Controls.ListBox>())
        {
            scroll.ScrollToVerticalOffset(0);
            window.UpdateLayout();
            var before = scroll.VerticalOffset;
            list.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120)
            { RoutedEvent = System.Windows.Input.Mouse.MouseWheelEvent });
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Console.WriteLine($"List wheel items={list.Items.Count} outerMoved={scroll.VerticalOffset > before}");
        }
    }

    private static AppSettings CreateLargeDesktopSessionSettings()
    {
        var settings = CreateDesktopSessionSettings();
        var task = settings.DesktopSessions.Tasks[0];
        return settings with
        {
            DesktopSessions = settings.DesktopSessions with
            {
                Tasks = [task with
                {
                    Steps = Enumerable.Range(0, 200)
                        .Select(_ => task.Steps[0] with { Id = Guid.NewGuid() }).ToArray()
                }]
            }
        };
    }

    private static void VerifyRdpConfiguration()
    {
        var control = new FloatDesk.App.DesktopSessions.RdpControlFactory().Create();
        var window = new DesktopSessionWindow(control, "RDP configuration probe");
        try
        {
            window.Show();
            window.UpdateLayout();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var task = control.ConfigureAsync(1280, 720, timeout.Token);
            while (!task.IsCompleted)
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            task.GetAwaiter().GetResult();
            Console.WriteLine("Real RDP ActiveX configuration PASS; Connect was not called.");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Real RDP ActiveX configuration FAIL: {exception}");
            throw;
        }
        finally
        {
            window.AllowClose();
            window.Close();
            control.Dispose();
        }
    }

    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    private sealed class ProbeApplication : System.Windows.Application
    {
        protected override void OnStartup(System.Windows.StartupEventArgs e) { }
    }

    private sealed class FixedSettingsCoordinator(AppSettings settings)
        : ISettingsCoordinator
    {
        public AppSettings Current { get; } = settings;
        public event Action<AppSettings>? Changed
        {
            add { }
            remove { }
        }

        public Task InitializeAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<OperationResult> UpdateAsync(
            Func<AppSettings, AppSettings> update,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Ok());
    }

    private sealed class NullCredentialStore : ITranslationCredentialStore
    {
        public Task<bool> HasAsync(
            TranslationProviderKind provider,
            string endpoint,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<string?> GetAsync(
            TranslationProviderKind provider,
            string endpoint,
            CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task SaveAsync(
            TranslationProviderKind provider,
            string endpoint,
            string apiKey,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteAsync(
            TranslationProviderKind provider,
            string endpoint,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private static async Task VerifyConsoleStartupFailureUiAsync()
    {
        // A headless owned process cannot expose a desktop pane. This verifies
        // failure presentation only; it is not a simulated successful backend.
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 45" }
        })!;
        var type = typeof(SettingsWindow).Assembly.GetType("FloatDesk.App.Views.HyperVConsoleWindow", true)!;
        var window = (System.Windows.Window)Activator.CreateInstance(type, [Guid.NewGuid(), "Startup failure probe", process, 1280, 720])!;
        try
        {
            window.Show();
            var attached = (Task)type.GetProperty("Attached", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            try
            {
                await attached.WaitAsync(TimeSpan.FromSeconds(35));
                throw new InvalidOperationException("A headless process must not be accepted as an RDP desktop.");
            }
            catch (InvalidOperationException exception) when (exception.Message.Contains("VMConnect", StringComparison.Ordinal)) { }
            window.UpdateLayout();
            if (!window.IsVisible) throw new InvalidOperationException("Initialization failure silently closed the console.");
            var viewport = (System.Windows.Controls.Grid)type.GetField("_viewport",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
            if (viewport.Children.Count != 1 || viewport.Children[0] is not System.Windows.Controls.TextBlock text ||
                !text.Text.Contains("控制台连接尚未完成", StringComparison.Ordinal))
                throw new InvalidOperationException("Initialization error is not visible in the console.");
            Console.WriteLine("console.failedStartupKeepsErrorWindow=PASS");
        }
        finally
        {
            window.Close();
            if (!process.HasExited) process.Kill();
        }
    }

    private static async Task VerifyConsoleClosePolicyAsync()
    {
        var type = typeof(SettingsWindow).Assembly.GetType("FloatDesk.App.Views.HyperVConsoleWindow", true)!;
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        // A failing policy must keep the console open with the reason, and the retry must close
        // without touching the virtual machine again. This is the regression the report describes:
        // closing used to do nothing at all, because the policy was inferred from the exit code of
        // the hosted console process.
        using (var failingProcess = StartSleeper())
        {
            var window = (System.Windows.Window)Activator.CreateInstance(type, [Guid.NewGuid(), "Close policy probe", failingProcess, 1280, 720])!;
            var calls = 0;
            try
            {
                type.GetProperty("ApplyClosePolicyAsync", flags)!.SetValue(window,
                    (Func<Task<string>>)(() =>
                    {
                        calls++;
                        return Task.FromException<string>(new InvalidOperationException("Synthetic close-policy failure"));
                    }));
                window.Show(); window.UpdateLayout();
                window.Close();
                await PumpAsync(200);
                if (!window.IsVisible) throw new InvalidOperationException("A failed close policy must keep the console open.");
                if (calls != 1) throw new InvalidOperationException($"Close policy ran {calls} times instead of once.");
                window.Close();
                await PumpAsync(200);
                if (window.IsVisible) throw new InvalidOperationException("The second close must close the console.");
                if (calls != 1) throw new InvalidOperationException("The retry must not run the close policy again.");
                Console.WriteLine("console.closePolicyFailureKeepsWindow=PASS");
            }
            finally { if (window.IsVisible) window.Close(); if (!failingProcess.HasExited) failingProcess.Kill(); }
        }

        using (var process = StartSleeper())
        {
            var window = (System.Windows.Window)Activator.CreateInstance(type, [Guid.NewGuid(), "Close policy probe", process, 1280, 720])!;
            var calls = 0;
            try
            {
                type.GetProperty("ApplyClosePolicyAsync", flags)!.SetValue(window,
                    (Func<Task<string>>)(() => { calls++; return Task.FromResult("已保存虚拟机状态并释放运行资源。"); }));
                window.Show(); window.UpdateLayout();
                window.Close();
                await PumpAsync(600);
                if (window.IsVisible) throw new InvalidOperationException("A successful close policy must close the console.");
                if (calls != 1) throw new InvalidOperationException($"Close policy ran {calls} times instead of once.");
                Console.WriteLine("console.closePolicyRunsOnClose=PASS");
            }
            finally { if (window.IsVisible) window.Close(); if (!process.HasExited) process.Kill(); }
        }

        using (var process = StartSleeper())
        {
            var window = (System.Windows.Window)Activator.CreateInstance(type, [Guid.NewGuid(), "Close policy probe", process, 1280, 720])!;
            var calls = 0;
            try
            {
                type.GetProperty("ApplyClosePolicyAsync", flags)!.SetValue(window,
                    (Func<Task<string>>)(() => { calls++; return Task.FromResult("skipped"); }));
                window.Show(); window.UpdateLayout();
                // Shutting the application down must not stop a machine the user never asked to stop.
                type.GetProperty("SkipClosePolicy", flags)!.SetValue(window, true);
                window.Close();
                await PumpAsync(300);
                if (window.IsVisible) throw new InvalidOperationException("Shutdown close must not be cancelled.");
                if (calls != 0) throw new InvalidOperationException("Shutdown close must not apply the close policy.");
                Console.WriteLine("console.closePolicySkippedOnShutdown=PASS");
            }
            finally { if (window.IsVisible) window.Close(); if (!process.HasExited) process.Kill(); }
        }
    }

    private static System.Diagnostics.Process StartSleeper() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 45" }
        })!;

    private static async Task PumpAsync(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(20);
        }
    }

    private static async Task VerifyConsolePowerUiAsync()
    {
        var type = typeof(SettingsWindow).Assembly.GetType("FloatDesk.App.Views.HyperVConsoleWindow", true)!;
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        foreach (var fail in new[] { true, false })
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 45" }
            })!;
            var window = (System.Windows.Window)Activator.CreateInstance(type, [Guid.NewGuid(), "Power behavior probe", process, 1280, 720])!;
            try
            {
                type.GetProperty("RunPowerScriptAsync", flags)!.SetValue(window,
                    (Func<string, Task<string>>)(script =>
                    {
                        if (!script.Contains("ShutdownReadbackMismatch", StringComparison.Ordinal)) throw new InvalidOperationException("Shutdown needs a state readback.");
                        return fail ? Task.FromException<string>(new InvalidOperationException("Synthetic shutdown failure")) : Task.FromResult("ok");
                    }));
                window.Show(); window.UpdateLayout();
                var dpi=System.Windows.Media.VisualTreeHelper.GetDpi(window);
                if(Math.Abs(window.MaxWidth-1280/dpi.DpiScaleX)>1||Math.Abs(window.MaxHeight-(720/dpi.DpiScaleY+78))>1)
                    throw new InvalidOperationException("Borderless console bounds must fit the guest viewport plus caption/status only.");
                GlassFrameProbe.VerifyScrollBarHit(window,(System.Windows.Controls.Primitives.ScrollBar)type.GetField("_vertical",flags)!.GetValue(window)!);
                if (VisualButtons(window).Any(button => button.ToolTip?.ToString()?.Contains("最大化", StringComparison.Ordinal) == true))
                    throw new InvalidOperationException("Removed maximize button is still present.");
                window.WindowState = System.Windows.WindowState.Maximized;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                if (window.WindowState == System.Windows.WindowState.Maximized) throw new InvalidOperationException("Console must not maximize.");
                await ((Task)type.GetMethod("OperateAsync", flags)!.Invoke(window, ["shutdown"])!);
                if (window.IsVisible == !fail) throw new InvalidOperationException("Shutdown success must close; failure must retain panel.");
                Console.WriteLine(fail ? "console.shutdownFailureKeepsPanel=PASS" : "console.shutdownSuccessClosesPanel=PASS");
            }
            finally { window.Close(); if (!process.HasExited) process.Kill(); }
        }
        static IEnumerable<System.Windows.Controls.Button> VisualButtons(System.Windows.DependencyObject root)
        {
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is System.Windows.Controls.Button button) yield return button;
                foreach (var nested in VisualButtons(child)) yield return nested;
            }
        }
    }

    private static async Task VerifyHyperVConsoleUiAsync(Guid vmId)
    {
        var type = typeof(SettingsWindow).Assembly.GetType("FloatDesk.App.DesktopSessions.HyperVConsoleController", true)!;
        using var controller = (IDisposable)Activator.CreateInstance(type)!;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sharedProbe = Environment.GetEnvironmentVariable("YUEYU_AUTH_RDP_SHARED_PATH");
        var task = (Task)type.GetMethod("ShowAsync")!.Invoke(controller,
            [vmId, 1280, 720, sharedProbe is null ? Array.Empty<string>() : new[] { sharedProbe }, (Func<Task<string>>)(() => { closed.TrySetResult(); return Task.FromResult("已按关闭设置处理虚拟机。"); }),
            CancellationToken.None, null, null, null, Environment.GetEnvironmentVariable("YUEYU_AUTHENTICATED_RDP_PROBE") == "1"])!;
        await task.WaitAsync(TimeSpan.FromSeconds(30));
        var window = System.Windows.Application.Current.Windows.Cast<System.Windows.Window>()
            .Single(item => item.GetType().Name == "HyperVConsoleWindow");
        window.Topmost = true;
        window.Left = 60; window.Top = 60;
        window.Activate();
        await Task.Delay(600);
        if (window.ActualWidth < 560 || window.ActualHeight < 360)
            throw new InvalidOperationException("Console viewport is not laid out.");
        Console.WriteLine($"console.visible={window.IsVisible};size={window.ActualWidth}x{window.ActualHeight}");
        window.Width = 900;
        window.Height = 600;
        await Task.Delay(250);
        Console.WriteLine("console.resize=PASS");
        var horizontalField = window.GetType().GetField("_horizontal", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var verticalField = window.GetType().GetField("_vertical", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var horizontal = (System.Windows.Controls.Primitives.ScrollBar)horizontalField.GetValue(window)!;
        var vertical = (System.Windows.Controls.Primitives.ScrollBar)verticalField.GetValue(window)!;
        if (horizontal.Maximum <= 0 || vertical.Maximum <= 0)
            throw new InvalidOperationException("Shrunk console must expose both desktop scroll ranges.");
        horizontal.Value = horizontal.Maximum;
        vertical.Value = vertical.Maximum;
        window.Width = window.MaxWidth;
        window.Height = window.MaxHeight;
        window.UpdateLayout();
        await Task.Delay(100);
        if (horizontal.Visibility != System.Windows.Visibility.Collapsed || vertical.Visibility != System.Windows.Visibility.Collapsed)
            throw new InvalidOperationException("Full resolution console should not show scrollbars.");
        window.Width = window.MaxWidth + 300;
        window.Height = window.MaxHeight + 300;
        window.UpdateLayout();
        if (window.ActualWidth > window.MaxWidth + 1 || window.ActualHeight > window.MaxHeight + 1)
            throw new InvalidOperationException("Console exceeded desktop bounds.");
        Console.WriteLine("console.boundsAndScroll=PASS");
        var startupVisible = (bool)window.GetType().GetProperty("NativeStartupVisible",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
        if (startupVisible) throw new InvalidOperationException("Native console became visible before embedding.");
        Console.WriteLine("console.hiddenStartup=PASS");
        var surface = window.GetType().GetField("_surface", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
        var nativeHandle = surface.GetType().GetField("_console", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(surface)!;
        var nativeStyle = (int)surface.GetType().GetMethod("GetWindowLong",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, [nativeHandle, -16])!;
        if ((nativeStyle & 0x40000000) != 0)
            throw new InvalidOperationException("Native RDP window must retain its top-level hierarchy.");
        Console.WriteLine("console.nativeTopLevelHierarchy=PASS");
        if ((nativeStyle & 0x00C40000) != 0)
            throw new InvalidOperationException("Native VMConnect caption or thick frame remains enabled behind the branded shell.");
        Console.WriteLine("console.nativeCaptionRemoved=PASS");
        var surfaceType = surface.GetType();
        var nativeMethods = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        surfaceType.GetMethod("SetWindowRgn", nativeMethods)!.Invoke(null, [nativeHandle, nint.Zero, true]);
        await Task.Delay(600);
        var liveProcess = (System.Diagnostics.Process)window.GetType().GetField("_process", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
        if (liveProcess.HasExited)
            throw new InvalidOperationException("Desktop client exited: " + window.GetType().GetProperty("NativeStartupError", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window));
        var region = (nint)surfaceType.GetMethod("CreateRectRgn", nativeMethods)!.Invoke(null, [0, 0, 0, 0])!;
        try
        {
            var regionKind = (int)surfaceType.GetMethod("GetWindowRgn", nativeMethods)!.Invoke(null, [nativeHandle, region])!;
            if (regionKind <= 1) throw new InvalidOperationException("Native console crop was not restored after an external reset.");
        }
        finally { surfaceType.GetMethod("DeleteObject", nativeMethods)!.Invoke(null, [region]); }
        var last = ((int X, int Y, int Width, int Height, int ViewWidth, int ViewHeight, int OffsetX, int OffsetY))
            surfaceType.GetField("_last", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(surface)!;
        var geometryMatches = surfaceType.GetMethod("GeometryMatches", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        bool Matches() => (bool)geometryMatches.Invoke(surface,
            [last.X, last.Y, last.Width, last.Height, last.ViewWidth, last.ViewHeight])!;
        if (!Matches()) throw new InvalidOperationException("Recovered crop must match the entire desktop viewport, including its bottom edge.");
        Console.WriteLine("console.cropResetRecovery=PASS");
        var instanceFields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var desktopOriginX = last.X + (int)surfaceType.GetField("_left", instanceFields)!.GetValue(surface)!;
        var desktopOriginY = last.Y + (int)surfaceType.GetField("_top", instanceFields)!.GetValue(surface)!;
        var viewportSize = (last.ViewWidth, last.ViewHeight);
        surfaceType.GetMethod("SetWindowPos", nativeMethods)!.Invoke(null,
            [nativeHandle, nint.Zero, last.X + 20, last.Y + 20, last.Width - 30, last.Height - 30, (uint)0x0010]);
        await Task.Delay(600);
        // Native toolbars can legitimately disappear during enhanced-session
        // connection. The outer frame may change; the desktop viewport must not.
        last = ((int X, int Y, int Width, int Height, int ViewWidth, int ViewHeight, int OffsetX, int OffsetY))
            surfaceType.GetField("_last", instanceFields)!.GetValue(surface)!;
        if (!Matches() || (last.ViewWidth, last.ViewHeight) != viewportSize ||
            last.X + (int)surfaceType.GetField("_left", instanceFields)!.GetValue(surface)! != desktopOriginX ||
            last.Y + (int)surfaceType.GetField("_top", instanceFields)!.GetValue(surface)! != desktopOriginY)
            throw new InvalidOperationException("Native console geometry was not restored after an external resize.");
        Console.WriteLine("console.externalResizeRecovery=PASS");
        var currentNativeStyle = (int)surfaceType.GetMethod("GetWindowLong", nativeMethods)!.Invoke(null, [nativeHandle, -16])!;
        surfaceType.GetMethod("SetWindowLong", nativeMethods)!.Invoke(null, [nativeHandle, -16, currentNativeStyle | 0x00CF0000]);
        surfaceType.GetMethod("SetWindowPos", nativeMethods)!.Invoke(null,
            [nativeHandle, nint.Zero, 0, 0, 0, 0, (uint)0x0037]);
        await Task.Delay(600);
        currentNativeStyle = (int)surfaceType.GetMethod("GetWindowLong", nativeMethods)!.Invoke(null, [nativeHandle, -16])!;
        if ((currentNativeStyle & 0x00CF0000) != 0)
            throw new InvalidOperationException("Native VMConnect chrome was not removed after a style reset.");
        Console.WriteLine("console.nativeCaptionResetRecovery=PASS");
        var actionsField = window.GetType().GetField("_actions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var actions = (System.Windows.Controls.StackPanel)actionsField.GetValue(window)!;
        foreach (System.Windows.Controls.Button button in actions.Children)
        {
            if (button.Content is not System.Windows.Controls.TextBlock || string.IsNullOrWhiteSpace(button.ToolTip?.ToString()) ||
                System.Windows.Controls.ToolTipService.GetInitialShowDelay(button) != 600)
                throw new InvalidOperationException("Power action must have an icon and delayed tooltip.");
        }
        Console.WriteLine("console.iconTooltips=PASS");
        var menuField = window.GetType().GetField("_menu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var menuButton = (System.Windows.Controls.Button)menuField.GetValue(window)!;
        menuButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        var popupField = window.GetType().GetField("_popup", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var popup = (System.Windows.Controls.Primitives.Popup)popupField.GetValue(window)!;
        if (!popup.IsOpen) throw new InvalidOperationException("Settings menu did not open.");
        Console.WriteLine("console.settingsMenu=PASS");
        popup.IsOpen = false;
        if (sharedProbe is not null)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            var notice = window.GetType().GetProperty("AuthenticationNotice", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            while (notice.GetValue(window) is not null && DateTime.UtcNow < deadline) await Task.Delay(250);
            if (notice.GetValue(window) is not null) throw new InvalidOperationException("Authenticated desktop login did not complete.");
            var entries = (System.Collections.IDictionary)type.GetField("_windows", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(controller)!;
            var entry = entries[vmId]!;
            var sharing = entry.GetType().GetProperty("Sharing")!.GetValue(entry)!;
            var drives = (IReadOnlyList<string>)sharing.GetType().GetProperty("Drives")!.GetValue(sharing)!;
            var results = await FloatDesk.Windows.DesktopSessions.HyperVGuestChecks.RunAsync(vmId,
                Guid.Parse("acaf10a6-f9f1-4201-bee1-626f298d8b33"),
                drives.Select(drive => $"if ([IO.File]::ReadAllText('\\\\tsclient\\{drive[0]}\\rdp-share-synthetic.txt').Trim() -ne 'yueyu-share-probe') {{ exit 1 }}").ToArray(), CancellationToken.None);
            if (results.Length != 1 || !results[0]) throw new InvalidOperationException("Guest could not read the selected synthetic shared folder.");
            Console.WriteLine("console.authenticatedFolderRedirection=PASS");
        }
        if (Environment.GetEnvironmentVariable("YUEYU_CONSOLE_SOAK_SECONDS") is { } soak &&
            int.TryParse(soak, out var seconds) && seconds > 0)
        {
            var until = DateTime.UtcNow.AddSeconds(Math.Min(seconds, 90));
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(1000);
                if (!window.IsVisible) throw new InvalidOperationException("Console closed during live RDP connection.");
                var process = (System.Diagnostics.Process)window.GetType().GetField("_process",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!;
                if (process.HasExited) throw new InvalidOperationException($"Native RDP process exited: 0x{process.ExitCode:X8}");
            }
            Console.WriteLine($"console.liveConnectionSoak={seconds}s PASS");
        }
        var screenshot = Environment.GetEnvironmentVariable("YUEYU_CONSOLE_SCREENSHOT");
        var nativeConcealed = (bool)surfaceType.Assembly.GetType("FloatDesk.App.Views.NativeConsoleStartupGuard", true)!
            .GetMethod("IsConcealed", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [nativeHandle])!;
        Console.WriteLine($"console.nativeConcealedAfterConnect={nativeConcealed}");
        if (nativeConcealed) throw new InvalidOperationException("Connected native desktop remains concealed.");
        if (!string.IsNullOrEmpty(screenshot))
        {
            var point = window.PointToScreen(new System.Windows.Point(0, 0));
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
            using var bitmap = new System.Drawing.Bitmap((int)(window.ActualWidth * dpi.DpiScaleX), (int)(window.ActualHeight * dpi.DpiScaleY));
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.CopyFromScreen((int)point.X, (int)point.Y, 0, 0, bitmap.Size);
            bitmap.Save(screenshot);
        }
        window.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Console.WriteLine("console.closeCallback=PASS");
    }

    private sealed class NullTranslationProviderFactory
        : ITranslationProviderFactory
    {
        public ITranslationProvider Create(
            TranslationSettings settings,
            string? apiKey) =>
            new NullTranslationProvider();
    }

    private sealed class NullTranslationProvider : ITranslationProvider
    {
        public string Id => "test";
        public string DisplayName => "Test";
        public TranslationProviderCapabilities Capabilities { get; } =
            new(false, 1_000, 1_000, 1, 1, TimeSpan.Zero);

        public Task<TranslationProviderResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(TranslationProviderResult.Ok(""));
    }
}
