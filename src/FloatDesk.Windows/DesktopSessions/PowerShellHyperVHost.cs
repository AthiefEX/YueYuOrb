using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace FloatDesk.Windows.DesktopSessions;

public sealed class PowerShellHyperVHost : IHyperVHost
{
    private readonly string _powerShellPath;

    public PowerShellHyperVHost(string powerShellPath = "powershell.exe")
    {
        if (string.IsNullOrWhiteSpace(powerShellPath))
            throw new ArgumentException(
                "A PowerShell executable path is required.",
                nameof(powerShellPath));
        _powerShellPath = powerShellPath;
    }

    public async Task<HyperVHostProbe> ProbeAsync(
        Guid vmId,
        CancellationToken cancellationToken)
    {
        var query = await QueryAsync(vmId, cancellationToken)
            .ConfigureAwait(false);
        return query.Probe;
    }

    public async Task<HyperVPowerState> GetPowerStateAsync(
        Guid vmId,
        CancellationToken cancellationToken)
    {
        var query = await QueryAsync(vmId, cancellationToken)
            .ConfigureAwait(false);
        return ParseState(query.State);
    }

    public Task StartVmAsync(
        Guid vmId,
        CancellationToken cancellationToken)
    {
        var id = vmId.ToString("D", CultureInfo.InvariantCulture);
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Import-Module Hyper-V -ErrorAction Stop
            $id = [guid]'{{id}}'
            $vm = Get-VM -Id $id -ErrorAction Stop
            if ($vm.State -ne 'Running') {
                Start-VM -VM $vm -ErrorAction Stop
            }
            $deadline = (Get-Date).AddSeconds(120)
            do {
                Start-Sleep -Milliseconds 500
                $vm = Get-VM -Id $id -ErrorAction Stop
                if ($vm.State -eq 'Running') { break }
            } while ((Get-Date) -lt $deadline)
            if ($vm.State -ne 'Running') {
                throw "VM did not reach Running state: $($vm.State)"
            }
            """;
        return RunScriptAsync(script, cancellationToken);
    }

    public Task StopVmAsync(
        Guid vmId,
        CancellationToken cancellationToken)
    {
        var id = vmId.ToString("D", CultureInfo.InvariantCulture);
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Import-Module Hyper-V -ErrorAction Stop
            $vm = Get-VM -Id ([guid]'{{id}}') -ErrorAction Stop
            if ($vm.State -ne 'Off') {
                Stop-VM -VM $vm -ErrorAction Stop
            }
            """;
        return RunScriptAsync(script, cancellationToken);
    }

    public Task<string> RunScriptForManagementAsyncCore(
        string script,
        CancellationToken cancellationToken) => RunScriptAsync(script, cancellationToken);

    private async Task<VmQuery> QueryAsync(
        Guid vmId,
        CancellationToken cancellationToken)
    {
        var id = vmId.ToString("D", CultureInfo.InvariantCulture);
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Import-Module Hyper-V -ErrorAction SilentlyContinue
            if ($null -eq (Get-Command Get-VM -ErrorAction SilentlyContinue)) {
                [pscustomobject]@{
                    available = $false
                    vmExists = $false
                    state = 'Unknown'
                    errorCode = 'HyperVUnavailable'
                } | ConvertTo-Json -Compress
                exit 0
            }
            $vm = Get-VM -Id ([guid]'{{id}}') -ErrorAction SilentlyContinue
            if ($null -eq $vm) {
                [pscustomobject]@{
                    available = $true
                    vmExists = $false
                    state = 'Unknown'
                    errorCode = 'VmNotFound'
                } | ConvertTo-Json -Compress
                exit 0
            }
            [pscustomobject]@{
                available = $true
                vmExists = $true
                state = [string]$vm.State
                errorCode = $null
            } | ConvertTo-Json -Compress
            """;
        var json = await RunScriptAsync(script, cancellationToken)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var available = root.GetProperty("available").GetBoolean();
        var vmExists = root.GetProperty("vmExists").GetBoolean();
        var state = root.GetProperty("state").GetString() ?? "Unknown";
        var errorCode = root.TryGetProperty("errorCode", out var error) &&
            error.ValueKind == JsonValueKind.String
            ? error.GetString()
            : null;
        return new VmQuery(
            new HyperVHostProbe(available, vmExists, errorCode),
            state);
    }

    private async Task<string> RunScriptAsync(
        string script,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = CreateStartInfo(script)
        };
        if (!process.Start())
        {
            throw new InvalidOperationException(
                "PowerShell could not be started for Hyper-V management.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(
            cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(
            cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw;
        }

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? "Hyper-V PowerShell management failed."
                    : error.Trim());
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException(
                "Hyper-V PowerShell management returned no data.");
        }

        return output.Trim();
    }

    private ProcessStartInfo CreateStartInfo(string script)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _powerShellPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);
        return startInfo;
    }

    private static HyperVPowerState ParseState(string state) =>
        state.ToUpperInvariant() switch
        {
            "OFF" => HyperVPowerState.Off,
            "SAVED" => HyperVPowerState.Saved,
            "RUNNING" => HyperVPowerState.Running,
            "PAUSED" => HyperVPowerState.Paused,
            _ => HyperVPowerState.Unknown
        };

    private sealed record VmQuery(
        HyperVHostProbe Probe,
        string State);
}
