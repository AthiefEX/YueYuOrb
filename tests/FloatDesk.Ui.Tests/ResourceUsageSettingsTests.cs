using FloatDesk.App.ViewModels;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class ResourceUsageSettingsTests
{
    [Fact]
    public void DefaultsRoundTripWithoutEnablingMonitoring()
    {
        var initial = AppSettings.CreateDefault();
        var vm = SettingsViewModel.From(initial);
        Assert.False(vm.ResourceLiquidEnabled);
        Assert.False(vm.ResourceRingsEnabled);
        Assert.Equal(ResourceMetric.Memory, vm.ResourceLiquidMetric);
        Assert.True(vm.ResourceShowDetails);
        Assert.Equal(.8, vm.ResourceHoverDelaySeconds);
        Assert.Equal(initial.ResourceUsage, vm.BuildCandidate(initial).ResourceUsage);
    }

    [Fact]
    public void EveryFieldMergesIndependentlyAgainstLatest()
    {
        var initial = AppSettings.CreateDefault();
        var edited = new ResourceUsageSettings
        {
            LiquidEnabled = true, RingsEnabled = true, LiquidMetric = ResourceMetric.Cpu,
            RingMemory = false, RingCpu = false, RingGpu = false,
            LiquidColor = "#123456", MemoryColor = "#AA123456", CpuColor = "#ABCDEF",
            GpuColor = "#112233", ShowDetails = false, HoverDelaySeconds = 2, GpuDeviceId = "gpu-b"
        };
        foreach (var property in typeof(ResourceUsageSettings).GetProperties().Where(p => p.CanWrite))
        {
            var vm = SettingsViewModel.From(initial);
            typeof(SettingsViewModel).GetProperty("Resource" + property.Name)!.SetValue(vm, property.GetValue(edited));
            var concurrent = initial.ResourceUsage with { LiquidColor = "#654321", HoverDelaySeconds = 3 };
            property.SetValue(concurrent, property.GetValue(initial.ResourceUsage));
            var expected = concurrent with { };
            property.SetValue(expected, property.GetValue(edited));
            var latest = initial with { ResourceUsage = concurrent };
            Assert.Equal(expected, vm.BuildCandidate(latest).ResourceUsage);
        }
        var external = initial with { ResourceUsage = edited with { RingMemory = true } };
        Assert.Equal(external.ResourceUsage, SettingsViewModel.From(initial).BuildCandidate(external).ResourceUsage);
    }

    [Fact]
    public void UserEditsWinConflictsAndUntouchedFieldsKeepLatest()
    {
        var initial = AppSettings.CreateDefault();
        var vm = SettingsViewModel.From(initial);
        vm.ResourceLiquidEnabled = true;
        vm.ResourceLiquidColor = "#123456";
        var latest = initial with { ResourceUsage = initial.ResourceUsage with
            { RingsEnabled = true, LiquidColor = "#654321", GpuDeviceId = "external", ShowDetails = false } };
        var result = vm.BuildCandidate(latest).ResourceUsage;
        Assert.True(result.LiquidEnabled);
        Assert.True(result.RingsEnabled);
        Assert.Equal("#123456", result.LiquidColor);
        Assert.Equal("external", result.GpuDeviceId);
        Assert.False(result.ShowDetails);
    }

    [Fact]
    public void EnabledEmptyRingsNormalizeToMemoryOnly()
    {
        var initial = AppSettings.CreateDefault();
        var vm = SettingsViewModel.From(initial);
        vm.ResourceRingsEnabled = true;
        vm.ResourceRingCpu = vm.ResourceRingMemory = vm.ResourceRingGpu = false;
        Assert.True(vm.TryBuildCandidate(initial, out var candidate, out _));
        Assert.True(candidate.ResourceUsage.RingMemory);
        Assert.False(candidate.ResourceUsage.RingCpu);
        Assert.False(candidate.ResourceUsage.RingGpu);
    }

    [Theory]
    [InlineData(.2, true)]
    [InlineData(5, true)]
    [InlineData(.199, false)]
    [InlineData(5.001, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    public void DelayEnforcesFiniteInclusiveBounds(double delay, bool valid)
    {
        var initial = AppSettings.CreateDefault();
        var vm = SettingsViewModel.From(initial);
        vm.ResourceHoverDelaySeconds = delay;
        Assert.Equal(valid, vm.TryBuildCandidate(initial, out var candidate, out var error));
        if (valid) Assert.Equal(delay, candidate.ResourceUsage.HoverDelaySeconds);
        else { Assert.Same(initial, candidate); Assert.Contains("0.2", error); Assert.Contains("5", error); }
    }

    [Theory]
    [InlineData("#abcdef", true)]
    [InlineData("#80ABCDEF", true)]
    [InlineData("#FFF", false)]
    [InlineData("red", false)]
    [InlineData("#12345G", false)]
    [InlineData("#123456789", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void EachColorRequiresSixOrEightHexDigits(string? color, bool valid)
    {
        var initial = AppSettings.CreateDefault();
        foreach (var name in new[] { "Liquid", "Memory", "Cpu", "Gpu" })
        {
            var vm = SettingsViewModel.From(initial);
            typeof(SettingsViewModel).GetProperty("Resource" + name + "Color")!.SetValue(vm, color);
            Assert.Equal(valid, vm.TryBuildCandidate(initial, out var candidate, out var error));
            if (valid) Assert.Equal(color, typeof(ResourceUsageSettings).GetProperty(name + "Color")!.GetValue(candidate.ResourceUsage));
            else { Assert.Same(initial, candidate); Assert.Contains("#RRGGBB", error); Assert.Contains("#AARRGGBB", error); }
        }
    }

    [Fact]
    public void MissingSavedGpuSurvivesDeviceRefreshAndCanReturn()
    {
        var initial = AppSettings.CreateDefault() with { ResourceUsage = new() { GpuDeviceId = "saved" } };
        var vm = SettingsViewModel.From(initial);
        Assert.Equal("saved", vm.ResourceGpuDeviceId);
        Assert.Contains(vm.ResourceGpuDevices, o => o.Id == "saved" && o.Name.Contains("不可用"));
        vm.SetResourceGpuDevices([new("other", "Other GPU", 50)]);
        Assert.Equal("saved", vm.ResourceGpuDeviceId);
        Assert.Equal("saved", vm.BuildCandidate(initial).ResourceUsage.GpuDeviceId);
        vm.SetResourceGpuDevices([new("saved", "Saved GPU", null)]);
        Assert.Equal("Saved GPU", Assert.Single(vm.ResourceGpuDevices, o => o.Id == "saved").Name);
        vm.SetResourceGpuDevices([]);
        Assert.Equal("saved", vm.ResourceGpuDeviceId);
        vm.ResourceGpuDeviceId = null;
        Assert.Null(vm.BuildCandidate(initial).ResourceUsage.GpuDeviceId);
        Assert.Contains(vm.ResourceGpuDevices, o => o.Id is null);
    }

    [Fact]
    public void RefreshKeepsSelectedOptionAndNotifiesBindings()
    {
        var vm = SettingsViewModel.From(AppSettings.CreateDefault());
        vm.SetResourceGpuDevices([new("a", "GPU A", 10), new("b", "GPU B", 20)]);
        vm.ResourceGpuDeviceId = "b";
        var selected = vm.ResourceGpuDevices.Single(o => o.Id == "b");
        // A WPF selector clears selection when its selected item is removed.
        vm.ResourceGpuDevices.CollectionChanged += (_, _) =>
        {
            if (!vm.ResourceGpuDevices.Contains(selected)) vm.ResourceGpuDeviceId = null;
        };
        vm.SetResourceGpuDevices([new("a", "GPU A", 30)]);
        Assert.Equal("b", vm.ResourceGpuDeviceId);
        Assert.Contains("不可用", selected.Name);
    }

    [Fact]
    public void ActualGpuComboRetainsMissingSelectionAndSavesAutomatic()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var initial = AppSettings.CreateDefault() with { ResourceUsage = new() { GpuDeviceId = "saved" } };
                var vm = SettingsViewModel.From(initial);
                var combo = new System.Windows.Controls.ComboBox
                {
                    ItemsSource = vm.ResourceGpuDevices, DisplayMemberPath = "Name", SelectedValuePath = "Id"
                };
                System.Windows.Data.BindingOperations.SetBinding(combo,
                    System.Windows.Controls.Primitives.Selector.SelectedValueProperty,
                    new System.Windows.Data.Binding(nameof(vm.ResourceGpuDeviceId))
                    { Source = vm, Mode = System.Windows.Data.BindingMode.TwoWay });
                Assert.Equal("saved", ((ResourceGpuDeviceOption)combo.SelectedItem).Id);
                vm.SetResourceGpuDevices([new("other", "Other GPU", null)]);
                Assert.Equal("saved", ((ResourceGpuDeviceOption)combo.SelectedItem).Id);
                vm.SetResourceGpuDevices([new("saved", "Saved GPU", 30)]);
                vm.SetResourceGpuDevices([]);
                Assert.Equal("saved", vm.ResourceGpuDeviceId);
                Assert.Contains("不可用", ((ResourceGpuDeviceOption)combo.SelectedItem).Name);
                combo.SelectedItem = vm.ResourceGpuDevices.Single(o => o.Id is null);
                Assert.Null(vm.ResourceGpuDeviceId);
                Assert.Null(vm.BuildCandidate(initial).ResourceUsage.GpuDeviceId);
                System.Windows.Data.BindingOperations.ClearAllBindings(combo);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
