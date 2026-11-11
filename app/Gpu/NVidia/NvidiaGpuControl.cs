using GHelper.Helpers;
using GHelper.Mode;
using NvAPIWrapper.GPU;
using NvAPIWrapper.Native;
using NvAPIWrapper.Native.GPU;
using NvAPIWrapper.Native.GPU.Structures;
using NvAPIWrapper.Native.Interfaces.GPU;
using System.Diagnostics;
using static NvAPIWrapper.Native.GPU.Structures.PerformanceStates20InfoV1;

namespace GHelper.Gpu.NVidia;

public class NvidiaGpuControl : IGpuControl
{

    public static int MaxCoreOffset = AppConfig.Get("max_gpu_core", 250);
    public static int MaxMemoryOffset = AppConfig.Get("max_gpu_memory", 500);

    public static int MinCoreOffset = AppConfig.Get("min_gpu_core", -250);
    public static int MinMemoryOffset = AppConfig.Get("min_gpu_memory", -500);

    public static int MinClockLimit = AppConfig.Get("min_gpu_clock", 400);
    public const int MaxClockLimit = 3000;

    private static PhysicalGPU? _internalGpu;

    public NvidiaGpuControl()
    {
        _internalGpu = GetInternalDiscreteGpu();
        if (IsValid)
        {
            if (FullName.Contains("5080") || FullName.Contains("5090"))
            {
                MaxCoreOffset = AppConfig.Get("max_gpu_core", 400);
                MaxMemoryOffset = AppConfig.Get("max_gpu_memory", 1000);
                Logger.WriteLine($"NVIDIA GPU: {FullName} ({MaxCoreOffset},{MaxMemoryOffset})");
            }
            if (FullName.Contains("5070 Ti") || FullName.Contains("4080") || FullName.Contains("4090"))
            {
                MaxCoreOffset = AppConfig.Get("max_gpu_core", 300);
                Logger.WriteLine($"NVIDIA GPU: {FullName} ({MaxCoreOffset},{MaxMemoryOffset})");
            }
        }
    }

    public bool IsValid => _internalGpu != null;

    public bool IsNvidia => IsValid;

    public string FullName => _internalGpu!.FullName;

    public int? _lastTemp;
    public int _lastTempTime = 0;

    private static bool verboseLog = false;

    private enum GpuState { Active, Asleep, Off }

    private GpuState _lastState = GpuState.Off;
    private long _lastStateTime = -StateCacheMs;
    private const int StateCacheMs = 500; 

    private GpuState GetGpuState()
    {
        if (!IsValid) return GpuState.Off;
        if (Environment.TickCount64 - _lastStateTime < StateCacheMs) return _lastState;
        try
        {
            var perfState = GPUApi.GetCurrentPerformanceState(_internalGpu!.Handle);
            if (verboseLog) Logger.WriteLine($"GPU: {perfState}");
            _lastState = GpuState.Active;
        }
        catch (Exception ex)
        {
            if (verboseLog) Logger.WriteLine($"GPU: {ex.Message}");
            _lastState = ex.Message == "NVAPI_GPU_NOT_POWERED" ? GpuState.Asleep : GpuState.Off;
        }
        _lastStateTime = Environment.TickCount64;
        return _lastState;
    }

    public int? ReadCurrentTemperature(bool log = false)
    {
        if (!IsValid) return null;

        var thermalSettings = GPUApi.GetThermalSettings(_internalGpu!.Handle);
        if (thermalSettings.Sensors is null) return null;

        IThermalSensor? gpuSensor = thermalSettings.Sensors
            .FirstOrDefault(s => s.Target == ThermalSettingsTarget.GPU);

        if (log || verboseLog) Logger.WriteLine($"GPU Temp: {gpuSensor?.CurrentTemperature}C");
        return gpuSensor?.CurrentTemperature;
    }

    private Task<int?>? _readTask;

    public int? GetCurrentTemperature()
    {
        if (!IsValid) return null;

        var state = GetGpuState();
        if (state == GpuState.Off) return null;

        if ((_readTask?.IsCompleted ?? true) && (state == GpuState.Active || ShouldRefresh()))
        {
            _readTask = Task.Run(() =>
            {
                var temp = ReadCurrentTemperature();
                if (temp is not null)
                {
                    _lastTemp = temp;
                    _lastTempTime = Environment.TickCount;
                }
                return temp;
            });
        }

        _readTask?.Wait(500);

        return _lastTemp;
    }

    private bool ShouldRefresh()
    {
        const int minInterval = 5_000;
        const int maxInterval = 120_000;
        const float deltaMin = 5f;
        const float deltaMax = 20f;

        if (_lastTemp is null) return true;

        var cpuTemp = (float)HardwareControl.GetCPUTemp();
        var delta = _lastTemp.Value - cpuTemp;

        if (delta < deltaMin) return false;

        var t = Math.Clamp((delta - deltaMin) / (deltaMax - deltaMin), 0f, 1f);
        var interval = (int)(maxInterval - t * (maxInterval - minInterval));

        var refresh = Environment.TickCount > _lastTempTime + interval;
        if (verboseLog) Logger.WriteLine($"GPU Temp Refresh Interval: {interval}ms {refresh}");

        return refresh;
    }

    public void Dispose()
    {
        _internalGpu = null;
    }

    private static readonly HashSet<string> _systemProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "dwm", "csrss", "winlogon", "services", "lsass", "smss", "wininit",
        "svchost", "fontdrvhost", "igfxem", "igfxhk", "igfxext",
        "nvcontainer", "nvdisplay.container", "nvsettings", "nvspcaps64",
        "nvsphelper64", "nvwmi64", "nvcplui", "atieclxx", "atiesrxx",
        "explorer", "taskhostw", "sihost", "runtimebroker", "shellexperiencehost",
        "searchhost", "startmenuexperiencehost", "textinputhost",
        "applicationframehost", "systemsettings", "dllhost", "conhost",
        "audiodg", "ctfloader", "spoolsv", "wlanext", "msdtc",
    };

    public void KillGPUApps()
    {
        if (!IsValid) return;
        PhysicalGPU internalGpu = _internalGpu!;

        int currentPid = Process.GetCurrentProcess().Id;

        try
        {
            Process[] processes = internalGpu.GetActiveApplications();
            foreach (Process process in processes)
                try
                {
                    if (process.Id == currentPid) continue;
                    if (process.SessionId == 0) continue;
                    if (_systemProcessNames.Contains(process.ProcessName)) continue;

                    Logger.WriteLine("Kill:" + process.ProcessName);
                    ProcessHelper.KillByProcess(process);
                }
                catch (Exception ex)
                {
                    Logger.WriteLine(ex.Message);
                }
        }
        catch (Exception ex)
        {
            Logger.WriteLine(ex.Message);
        }

        //GeneralApi.RestartDisplayDriver();
    }


    public bool IsClockOffsetSupported { get; private set; } = true;

    public bool GetClocks(out int core, out int memory)
    {
        PhysicalGPU internalGpu = _internalGpu!;

        //Logger.WriteLine(internalGpu.FullName);
        //Logger.WriteLine(internalGpu.ArchitectInformation.ToString());

        try
        {
            var temp = ReadCurrentTemperature(true); // Force wake up GPU for clock reading

            IPerformanceStates20Info states = GPUApi.GetPerformanceStates20(internalGpu.Handle);
            core = states.Clocks[PerformanceStateId.P0_3DPerformance][0].FrequencyDeltaInkHz.DeltaValue / 1000;
            memory = states.Clocks[PerformanceStateId.P0_3DPerformance][1].FrequencyDeltaInkHz.DeltaValue / 1000;
            var range = states.Clocks[PerformanceStateId.P0_3DPerformance][0].FrequencyDeltaInkHz.DeltaRange;
            IsClockOffsetSupported = range.Maximum > range.Minimum;
            Logger.WriteLine($"GET GPU CLOCKS: {core}, {memory}");

            foreach (var delta in states.Voltages[PerformanceStateId.P0_3DPerformance])
            {
                Logger.WriteLine("GPU VOLT:" + delta.IsEditable + " - " + delta.ValueDeltaInMicroVolt.DeltaValue);
            }

            return true;

        }
        catch (Exception ex)
        {
            Logger.WriteLine("GET GPU CLOCKS:" + ex.Message);
            core = memory = 0;
            return false;
        }

    }


    private static bool RunPowershellCommand(string script, int timeoutMs = 0)
    {
        try
        {
            ProcessHelper.RunCMD("powershell", script, timeoutMs: timeoutMs);
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine(ex.ToString());
            return false;
        }

    }

    public int GetMaxGPUCLock()
    {
        PhysicalGPU internalGpu = _internalGpu!;
        try
        {
            PrivateClockBoostLockV2 data = GPUApi.GetClockBoostLock(internalGpu.Handle);
            int limit = (int)data.ClockBoostLocks[0].VoltageInMicroV / 1000;
            Logger.WriteLine("GET CLOCK LIMIT: " + limit);
            return limit;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("GET CLOCK LIMIT: " + ex.Message);
            return -1;

        }
    }


    public int SetMaxGPUClock(int clock)
    {

        if (clock < MinClockLimit || clock >= MaxClockLimit) clock = 0;

        int _clockLimit = GetMaxGPUCLock();

        if (_clockLimit == clock) return 0;

        if (clock > 0) RunPowershellCommand($"nvidia-smi -lgc 0,{clock}");
        else RunPowershellCommand($"nvidia-smi -rgc");
        return 1;


    }

    public static void RestartNvContainer()
    {
        if (!ProcessHelper.IsUserAdministrator()) return;
        RunPowershellCommand(@"Restart-Service -Name 'NvContainerLocalSystem' -Force", 30000);
    }

    public static void RestartNVService()
    {
        if (!ProcessHelper.IsUserAdministrator()) return;
        RunPowershellCommand(@"Restart-Service -Name 'NVDisplay.ContainerLocalSystem' -Force", 30000);
        RunPowershellCommand(@"Restart-Service -Name 'NvContainerLocalSystem' -Force", 30000);
    }

    public static void StopNVService()
    {
        if (!ProcessHelper.IsUserAdministrator()) return;
        RunPowershellCommand(@"Stop-Service -Name 'NvContainerLocalSystem' -Force", 30000);
        RunPowershellCommand(@"Stop-Service -Name 'NVDisplay.ContainerLocalSystem' -Force", 30000);
    }

    public int SetClocks(int core, int memory)
    {

        if (core < MinCoreOffset || core > MaxCoreOffset) return 0;
        if (memory < MinMemoryOffset || memory > MaxMemoryOffset) return 0;

        GetClocks(out int currentCore, out int currentMemory);

        // Nothing to set
        if (Math.Abs(core - currentCore) < 5 && Math.Abs(memory - currentMemory) < 5) return 0;

        PhysicalGPU internalGpu = _internalGpu!;

        var coreClock = new PerformanceStates20ClockEntryV1(PublicClockDomain.Graphics, new PerformanceStates20ParameterDelta(core * 1000));
        var memoryClock = new PerformanceStates20ClockEntryV1(PublicClockDomain.Memory, new PerformanceStates20ParameterDelta(memory * 1000));
        //var voltageEntry = new PerformanceStates20BaseVoltageEntryV1(PerformanceVoltageDomain.Core, new PerformanceStates20ParameterDelta(voltage));

        PerformanceStates20ClockEntryV1[] clocks = { coreClock, memoryClock };
        PerformanceStates20BaseVoltageEntryV1[] voltages = { };

        PerformanceState20[] performanceStates = { new PerformanceState20(PerformanceStateId.P0_3DPerformance, clocks, voltages) };

        var overclock = new PerformanceStates20InfoV1(performanceStates, 2, 0);

        try
        {
            Logger.WriteLine($"SET GPU CLOCKS: {core}, {memory}");
            GPUApi.SetPerformanceStates20(internalGpu.Handle, overclock);
        }
        catch (Exception ex)
        {
            Logger.WriteLine("SET GPU CLOCKS: " + ex.Message);
            return -1;
        }

        return 1;
    }

    private static PhysicalGPU? GetInternalDiscreteGpu()
    {
        try
        {
            return PhysicalGPU
                .GetPhysicalGPUs()
                .FirstOrDefault(gpu => gpu.SystemType == SystemType.Laptop);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
            return null;
        }
    }


    public int? GetGpuUse()
    {
        if (!IsValid) return null;
         if (GetGpuState() != GpuState.Active) return null;

        PhysicalGPU internalGpu = _internalGpu!;
        IUtilizationDomainInfo? gpuUsage = GPUApi.GetUsages(internalGpu.Handle).GPU;

        return (int?)gpuUsage?.Percentage;

    }


    public float? GetGpuPower()
    {
        if (!IsValid) return null;
        var state = GetGpuState();
        if (state == GpuState.Off)
        {
            NvmlHelper.Shutdown();
            return null;
        }
        if (state != GpuState.Active) return 0f;
        return NvmlHelper.GetGpuPower() ?? 0f;
    }

    public (long usedMb, long totalMb)? GetVramInfo()
    {
        if (!IsValid) return null;
        if (GetGpuState() != GpuState.Active) return null;
        return NvmlHelper.GetMemoryInfo();
    }

    public static string TryApplyPolicyWatts(int watts)
    {
        if (!NvidiaWattWrite.MayAttempt(NvidiaWattTrigger.ExplicitClick, XgDockSnapshot.Unsampled(), false))
            return "NVIDIA watt writes are disabled. A driver rejection is not a lock.";
        if (!TryPolicyEntry(out var entry, out string listing))
            return listing;
        if (!TryReferenceWatts(out double reference))
            return $"NVML refused a watt limit. NVIDIA policy range is {Percent(entry.MinimumPowerInPCM)}–{Percent(entry.MaximumPowerInPCM)}%. No watt reference was read, so {watts}W was not written.";
        double referencePercent = Percent(entry.DefaultPowerInPCM);
        if (referencePercent <= 0) referencePercent = 100;
        if (!NvidiaPolicyTarget.TryTargetPcm(watts, reference, referencePercent, entry.MinimumPowerInPCM, entry.MaximumPowerInPCM, out uint pcm))
        {
            double minW = NvidiaPolicyTarget.WattsForPcm(entry.MinimumPowerInPCM, reference, referencePercent);
            double maxW = NvidiaPolicyTarget.WattsForPcm(entry.MaximumPowerInPCM, reference, referencePercent);
            return $"The NVIDIA policy allows {minW:0}–{maxW:0}W. {watts}W was not written.";
        }
        return WritePolicy(pcm, $"The NVIDIA policy target is {pcm / 1000.0:0.#}% of the {reference:0}W default.");
    }

    public static string TryRestorePolicyDefault()
    {
        if (!NvidiaWattWrite.MayAttempt(NvidiaWattTrigger.ExplicitClick, XgDockSnapshot.Unsampled(), false))
            return "NVIDIA watt writes are disabled. A driver rejection is not a lock.";
        if (!TryPolicyEntry(out var entry, out string listing))
            return listing;
        return WritePolicy(entry.DefaultPowerInPCM, $"The NVIDIA policy target was restored to {Percent(entry.DefaultPowerInPCM):0.#}%.");
    }

    static bool TryPolicyEntry(out PrivatePowerPoliciesInfoV1.PowerPolicyInfoEntry entry, out string listing)
    {
        entry = default;
        listing = "The NVIDIA GPU is not available for a policy limit.";
        if (_internalGpu is null) return false;
        try
        {
            var entries = GPUApi.ClientPowerPoliciesGetInfo(_internalGpu.Handle).PowerPolicyInfoEntries;
            if (entries is null || entries.Length == 0)
            {
                listing = "The NVIDIA policy list is empty.";
                return false;
            }
            listing = string.Join("; ", entries.Select(item =>
                $"{item.PerformanceStateId} {Percent(item.MinimumPowerInPCM):0.#}-{Percent(item.MaximumPowerInPCM):0.#}% default {Percent(item.DefaultPowerInPCM):0.#}%"));
            Logger.WriteLine("NVAPI power policy: " + listing);
            int index = Array.FindIndex(entries, item => item.PerformanceStateId == PerformanceStateId.P0_3DPerformance);
            if (index < 0)
            {
                listing = "The NVIDIA policy has no P0 entry. " + listing;
                return false;
            }
            entry = entries[index];
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("NVAPI power policy: " + ex.GetType().Name + " " + ex.Message);
            listing = "The NVIDIA policy interface refused the power limit (" + ex.GetType().Name + ").";
            return false;
        }
    }

    static bool TryReferenceWatts(out double reference)
    {
        if (NvidiaSmi.TryReadLimitWatts("power.default_limit", out reference)) return true;
        return NvidiaSmi.TryReadLimitWatts("power.max_limit", out reference);
    }

    static string WritePolicy(uint pcm, string success)
    {
        if (_internalGpu is null) return "The NVIDIA GPU is not available for a policy limit.";
        try
        {
            var status = new PrivatePowerPoliciesStatusV1(new[]
            {
                new PrivatePowerPoliciesStatusV1.PowerPolicyStatusEntry(pcm)
            });
            GPUApi.ClientPowerPoliciesSetStatus(_internalGpu.Handle, status);
            var readback = GPUApi.ClientPowerPoliciesGetStatus(_internalGpu.Handle).PowerPolicyStatusEntries;
            uint? got = readback is { Length: > 0 } ? readback[0].PowerTargetInPCM : null;
            Logger.WriteLine($"NVAPI power policy write pcm={pcm} readback={got?.ToString() ?? "none"}");
            if (got is uint value && Math.Abs((long)value - pcm) <= 1000)
                return success;
            return $"The NVIDIA policy call returned, and the target still reads {(got is uint seen ? (seen / 1000.0).ToString("0.#") + "%" : "nothing")}.";
        }
        catch (Exception ex)
        {
            Logger.WriteLine("NVAPI power policy write: " + ex.GetType().Name + " " + ex.Message);
            return "The NVIDIA policy interface refused the power limit (" + ex.GetType().Name + ").";
        }
    }

    static double Percent(uint pcm) => pcm / 1000.0;

}
