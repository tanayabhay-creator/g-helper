using GHelper.Mode;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

public static class NvmlHelper
{
    const string NvmlDll = "nvml.dll";

    [DllImport(NvmlDll)] static extern int nvmlInit_v2();
    [DllImport(NvmlDll)] static extern int nvmlShutdown();
    [DllImport(NvmlDll)] static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport(NvmlDll)] static extern int nvmlDeviceGetTemperature(IntPtr device, uint sensorType, out uint temp);
    [DllImport(NvmlDll)] static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint powerMilliWatts);
    [DllImport(NvmlDll)] static extern int nvmlDeviceGetPowerManagementLimitConstraints(IntPtr device, out uint minMilliWatts, out uint maxMilliWatts);
    [DllImport(NvmlDll)] static extern int nvmlDeviceGetPowerManagementLimit(IntPtr device, out uint milliWatts);
    [DllImport(NvmlDll)] static extern int nvmlDeviceSetPowerManagementLimit(IntPtr device, uint milliWatts);
    [DllImport(NvmlDll)] static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out nvmlMemory_t memory);

    [StructLayout(LayoutKind.Sequential)]
    struct nvmlMemory_t { public ulong total; public ulong free; public ulong used; }

    const uint NVML_TEMPERATURE_GPU = 0;
    const int NVML_SUCCESS = 0;
    const int NVML_ERROR_NOT_SUPPORTED = 3;
    const int NVML_ERROR_NO_PERMISSION = 4;

    public readonly record struct PowerCapResult(bool Accepted, int RequestedWatts, int? AppliedWatts, int? MinWatts, int? MaxWatts, int Status, string Note);

    public static bool InRange(int requestedWatts, int? minWatts, int? maxWatts)
        => requestedWatts > 0
           && (minWatts is null || requestedWatts >= minWatts)
           && (maxWatts is null || requestedWatts <= maxWatts);

    private static readonly object _lock = new();
    private static bool _init = false;

    public static void Init()
    {
        if (_init) return;
        try
        {
            int rc = nvmlInit_v2();
            _init = rc == NVML_SUCCESS;
            Logger.WriteLine($"NVML Init: {rc}");
        }
        catch (Exception e)
        {
            Logger.WriteLine($"NVML Init exception: {e.Message}");
        }
    }

    public static int? GetTemperature(uint gpuIndex = 0)
    {
        lock (_lock)
        {
            Init();
            if (!_init) return null;
            try
            {
                if (nvmlDeviceGetHandleByIndex_v2(gpuIndex, out IntPtr device) != NVML_SUCCESS) return null;
                if (nvmlDeviceGetTemperature(device, NVML_TEMPERATURE_GPU, out uint temp) != NVML_SUCCESS) return null;
                return (int)temp;
            }
            catch { return null; }
        }
    }

    public static float? GetGpuPower(uint gpuIndex = 0)
    {
        lock (_lock)
        {
            Init();
            if (!_init) return null;
            try
            {
                if (nvmlDeviceGetHandleByIndex_v2(gpuIndex, out IntPtr device) != NVML_SUCCESS) return null;
                if (nvmlDeviceGetPowerUsage(device, out uint mW) != NVML_SUCCESS) return null;
                if (mW > 200_000f) return null;
                return mW / 1000f;
            }
            catch { return null; }
        }
    }

    public static (long usedMb, long totalMb)? GetMemoryInfo(uint gpuIndex = 0)
    {
        lock (_lock)
        {
            Init();
            if (!_init) return null;
            try
            {
                if (nvmlDeviceGetHandleByIndex_v2(gpuIndex, out IntPtr device) != NVML_SUCCESS) return null;
                if (nvmlDeviceGetMemoryInfo(device, out nvmlMemory_t mem) != NVML_SUCCESS) return null;
                if (mem.total == 0) return null;
                const ulong MB = 1024 * 1024;
                return ((long)(mem.used / MB), (long)(mem.total / MB));
            }
            catch { return null; }
        }
    }

    public static PowerCapResult TrySetPowerLimit(int watts, bool allowElevation, uint gpuIndex = 0)
    {
        if (!NvidiaWattWrite.MayAttempt(NvidiaWattTrigger.ExplicitClick, XgDockSnapshot.Unsampled(), false))
            return new PowerCapResult(false, watts, null, null, null, -1,
                "NVIDIA watt writes are disabled. A driver rejection is not a lock.");
        lock (_lock)
        {
            var result = TrySetPowerLimitLocked(watts, gpuIndex);
            if (result.Accepted || result.Status != NVML_ERROR_NO_PERMISSION || !allowElevation)
            {
                Logger.WriteLine(result.Note);
                return result;
            }
        }

        bool started = TryElevatedNvidiaSmi(watts);
        lock (_lock)
        {
            int? applied = TryReadLimit(gpuIndex);
            bool accepted = applied == watts;
            string note = accepted
                ? $"The NVIDIA driver reports a {watts}W limit."
                : started
                    ? $"nvidia-smi ran, and the driver still reports {applied?.ToString() ?? "no"}W."
                    : $"Windows did not approve nvidia-smi. {watts}W was not applied.";
            var result = new PowerCapResult(accepted, watts, applied, null, null, NVML_ERROR_NO_PERMISSION, note);
            Logger.WriteLine(result.Note);
            return result;
        }
    }

    public static PowerCapResult TryReleasePowerLimit(bool allowElevation, uint gpuIndex = 0)
    {
        if (!NvidiaWattWrite.MayAttempt(NvidiaWattTrigger.ExplicitClick, XgDockSnapshot.Unsampled(), false))
            return new PowerCapResult(false, 0, null, null, null, -1,
                "NVIDIA watt writes are disabled. A driver rejection is not a lock.");
        int max;
        lock (_lock)
        {
            if (!TryReadConstraints(gpuIndex, out int? min, out int? maxWatts) || maxWatts is null)
            {
                var failed = new PowerCapResult(false, 0, TryReadLimit(gpuIndex), min, maxWatts, NVML_ERROR_NOT_SUPPORTED,
                    "The NVIDIA driver did not report a maximum power limit to restore.");
                Logger.WriteLine(failed.Note);
                return failed;
            }
            max = maxWatts.Value;
        }
        return TrySetPowerLimit(max, allowElevation, gpuIndex);
    }

    static PowerCapResult TrySetPowerLimitLocked(int watts, uint gpuIndex)
    {
        Init();
        if (!_init)
            return new PowerCapResult(false, watts, null, null, null, -1, "NVML is not available. The power limit was not applied.");
        if (watts <= 0)
            return new PowerCapResult(false, watts, null, null, null, -1, "There is no GPU watt value to apply.");
        if (!TryReadConstraints(gpuIndex, out int? min, out int? max))
            min = max = null;
        if (!InRange(watts, min, max))
        {
            string range = min is int lo && max is int hi ? $"{lo}–{hi}W" : "its allowed range";
            return new PowerCapResult(false, watts, TryReadLimit(gpuIndex), min, max, NVML_ERROR_NOT_SUPPORTED,
                $"The driver allows {range}. {watts}W was not written.");
        }
        if (nvmlDeviceGetHandleByIndex_v2(gpuIndex, out IntPtr device) != NVML_SUCCESS)
            return new PowerCapResult(false, watts, null, min, max, -1, "The NVIDIA GPU did not answer a power-limit request.");

        int status = nvmlDeviceSetPowerManagementLimit(device, (uint)watts * 1000);
        int? applied = TryReadLimit(gpuIndex);
        bool accepted = status == NVML_SUCCESS && applied == watts;
        string note = status switch
        {
            NVML_SUCCESS when accepted => $"The NVIDIA driver reports a {watts}W limit.",
            NVML_SUCCESS => $"The NVIDIA driver accepted the call, and it still reports {applied?.ToString() ?? "no"}W.",
            NVML_ERROR_NOT_SUPPORTED => "The NVIDIA driver does not allow a power limit on this GPU.",
            NVML_ERROR_NO_PERMISSION => "The NVIDIA driver refused the power limit without administrator approval.",
            _ => $"The NVIDIA driver returned status {status}. {watts}W was not applied."
        };
        return new PowerCapResult(accepted, watts, applied, min, max, status, note);
    }

    static bool TryReadConstraints(uint gpuIndex, out int? minWatts, out int? maxWatts)
    {
        minWatts = null;
        maxWatts = null;
        Init();
        if (!_init) return false;
        if (nvmlDeviceGetHandleByIndex_v2(gpuIndex, out IntPtr device) != NVML_SUCCESS) return false;
        if (nvmlDeviceGetPowerManagementLimitConstraints(device, out uint min, out uint max) != NVML_SUCCESS) return false;
        minWatts = (int)(min / 1000);
        maxWatts = (int)(max / 1000);
        return maxWatts >= minWatts && maxWatts > 0;
    }

    static int? TryReadLimit(uint gpuIndex)
    {
        Init();
        if (!_init) return null;
        if (nvmlDeviceGetHandleByIndex_v2(gpuIndex, out IntPtr device) != NVML_SUCCESS) return null;
        if (nvmlDeviceGetPowerManagementLimit(device, out uint limit) != NVML_SUCCESS) return null;
        return (int)(limit / 1000);
    }

    static bool TryElevatedNvidiaSmi(int watts)
    {
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "-i 0 -pl " + watts.ToString(CultureInfo.InvariantCulture),
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using var process = Process.Start(start);
            if (process is null) return false;
            return process.WaitForExit(20000) && process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("nvidia-smi -pl was not started: " + ex.Message);
            return false;
        }
    }

    public static void Shutdown()
    {
        lock (_lock)
        {
            if (!_init) return;
            try
            {
                int rc = nvmlShutdown();
                Logger.WriteLine($"NVML Shutdown: {rc}");
            }
            catch { }
            _init = false;
        }
    }
}
