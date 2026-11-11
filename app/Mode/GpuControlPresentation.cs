using System.Reflection;

namespace GHelper.Mode;

// Upstream range: nvidia-smi power.max_limit when it parses, otherwise the model fallback.
// The fallback is not a measured XG Mobile limit. A missing GPU_BASE still has no range.
public static class GpuPowerLimit
{
    public static int AdditionalMaximum(int gpuBase, int reportedMax)
        => reportedMax - gpuBase - AsusACPI.MaxGPUBoost;

    public static bool PowerInRange(int gpuPower)
        => gpuPower >= AsusACPI.MinGPUPower && gpuPower <= AsusACPI.MaxGPUPower;

    public static bool BoostInRange(int gpuBoost)
        => gpuBoost >= AsusACPI.MinGPUBoost && gpuBoost <= AsusACPI.MaxGPUBoost;

    public static bool TempInRange(int gpuTemp)
        => gpuTemp >= AsusACPI.MinGPUTemp && gpuTemp <= AsusACPI.MaxGPUTemp;
}

public static class GpuPowerCapability
{
    public readonly record struct Result(
        bool BasePositive,
        bool PowerResponds,
        bool RangeEstablished,
        int? AdditionalMinimum,
        int? AdditionalMaximum,
        int? MeasuredMaxLimit,
        int? RangeLimit,
        string RangeSource,
        DecodedRead BaseRead,
        DecodedRead PowerRead)
    {
        public static Result Unavailable { get; } = new(
            false, false, false, null, null, null, null, "unavailable", default, default);
    }

    public static bool TransportOk(DecodedRead read)
        => read.IoctlSucceeded && read.BytesReturned >= 4 && read.Buffer != null && read.Buffer.Length >= 4;

    // Upstream DeviceGet uses the first four buffer bytes. A short reported length does not discard them.
    public static int? PositiveBase(DecodedRead read)
        => read.Buffer != null && read.Buffer.Length >= 4 && read.Decoded > 0 ? read.Decoded : null;

    public static Result Evaluate(DecodedRead gpuBase, DecodedRead gpuPower, int? measuredMaxLimit, int? fallbackMaxLimit)
    {
        int? positiveBase = PositiveBase(gpuBase);
        bool basePositive = positiveBase is not null;
        bool powerResponds = TransportOk(gpuPower) && gpuPower.Decoded >= 0;
        if (positiveBase is int gpuBaseWatts && TryAdditional(gpuBaseWatts, measuredMaxLimit, out int measuredAdditional))
        {
            return new Result(
                true, powerResponds, true,
                AsusACPI.MinGPUPower, measuredAdditional, measuredMaxLimit, measuredMaxLimit, "nvidia-smi",
                gpuBase, gpuPower);
        }

        if (positiveBase is int fallbackBase && TryAdditional(fallbackBase, fallbackMaxLimit, out int fallbackAdditional))
        {
            return new Result(
                true, powerResponds, true,
                AsusACPI.MinGPUPower, fallbackAdditional, measuredMaxLimit, fallbackMaxLimit, "model-fallback",
                gpuBase, gpuPower);
        }

        return new Result(
            basePositive, powerResponds, false, null, null, measuredMaxLimit, null, "unavailable",
            gpuBase, gpuPower);
    }

    static bool TryAdditional(int gpuBase, int? limit, out int additional)
    {
        additional = 0;
        if (limit is not int reported) return false;
        additional = GpuPowerLimit.AdditionalMaximum(gpuBase, reported);
        return additional >= AsusACPI.MinGPUPower;
    }
}

public static class GpuControlPresentation
{
    public const string CapabilityUnavailableText =
        "GPU_BASE did not return a positive reading. GPU Power is unavailable.";
    public const string GpuPowerUnavailableText =
        "GPU_POWER did not answer. GPU Power is unavailable.";
    public const string GpuCapabilityUnavailableText =
        "GPU_BASE and GPU_POWER did not answer. GPU Power is unavailable.";
    public const string RangeUnavailableText =
        "GPU power range cannot be established from the current readings. No watt value is shown.";
    public const string WattUnavailableLabel = "Unavailable";

    public static string BuildVersion { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    public readonly record struct Decision(
        bool PageVisible,
        bool CpuTdpEnabled,
        bool GpuPowerVisible,
        bool GpuPowerEnabled,
        bool GpuBoostVisible,
        bool GpuBoostEnabled,
        bool GpuTempVisible,
        bool GpuTempEnabled,
        bool ClockOffsetVisible,
        bool ClockLimitVisible,
        string? Notice,
        string Reason,
        bool ShowWattValue,
        string Diagnostic);

    public readonly record struct BoundControls(
        bool PageVisible,
        bool PowerRowVisible,
        bool PowerEnabled,
        bool BoostRowVisible,
        bool BoostEnabled,
        bool TempRowVisible,
        bool TempEnabled,
        bool ClockOffsetVisible,
        bool ClockLimitVisible,
        bool NoticeVisible,
        string Notice,
        string PowerLabel,
        string Diagnostic,
        bool ApplyLimitsEnabled);

    public static Decision Decide(
        XgDockSnapshot snap,
        bool admissionClosed,
        bool mitigationEnabled,
        bool nvidiaController,
        GpuPowerCapability.Result power,
        bool gpuBoostSupported,
        bool gpuTempSupported,
        bool clockOffsetSupported,
        bool gpuEco,
        string? initFault,
        string? buildVersion = null)
    {
        bool connected = snap.HasSample && XgDockClassifier.Classify(snap.Connected) == XgValueClass.One;
        bool writesOpen = mitigationEnabled
            ? snap.HasSample && snap.State == XgDockState.Inactive && !admissionClosed
            : snap.State != XgDockState.Active;
        bool ecoHidesPage = gpuEco && !(mitigationEnabled && connected);
        bool pageVisible = !ecoHidesPage && (mitigationEnabled ? nvidiaController || connected : nvidiaController);
        bool keepPowerRow = mitigationEnabled && connected;
        bool powerVisible = pageVisible && (keepPowerRow || power.BasePositive);
        bool capabilityReady = power.BasePositive && power.PowerResponds && power.RangeEstablished;
        bool powerEnabled = powerVisible && writesOpen && capabilityReady;

        string reason;
        string? notice;
        if (!pageVisible)
        {
            reason = "page_hidden";
            notice = null;
        }
        else if (!writesOpen)
        {
            reason = "locked";
            notice = XgMitigation.GpuLimitsLockedText;
        }
        else if (!power.BasePositive && !power.PowerResponds)
        {
            reason = "gpu_base_unavailable";
            notice = GpuCapabilityUnavailableText;
        }
        else if (!power.BasePositive)
        {
            reason = "gpu_base_unavailable";
            notice = CapabilityUnavailableText;
        }
        else if (!power.PowerResponds)
        {
            reason = "gpu_power_unavailable";
            notice = GpuPowerUnavailableText;
        }
        else if (!power.RangeEstablished)
        {
            reason = "range_unavailable";
            notice = RangeUnavailableText;
        }
        else
        {
            reason = "editable";
            notice = null;
        }

        var decision = new Decision(
            pageVisible,
            writesOpen,
            powerVisible,
            powerEnabled,
            pageVisible && gpuBoostSupported,
            pageVisible && gpuBoostSupported && writesOpen,
            pageVisible && gpuTempSupported,
            pageVisible && gpuTempSupported && writesOpen,
            pageVisible && nvidiaController && clockOffsetSupported && writesOpen,
            pageVisible && nvidiaController && writesOpen,
            notice,
            reason,
            capabilityReady,
            "");
        return decision with
        {
            Diagnostic = Diagnostic(buildVersion ?? BuildVersion, decision, power, snap, admissionClosed, gpuEco, initFault)
        };
    }

    public static BoundControls Bind(Decision decision, string wattLabel)
    {
        return new BoundControls(
            decision.PageVisible,
            decision.GpuPowerVisible,
            decision.GpuPowerEnabled,
            decision.GpuBoostVisible,
            decision.GpuBoostEnabled,
            decision.GpuTempVisible,
            decision.GpuTempEnabled,
            decision.ClockOffsetVisible,
            decision.ClockLimitVisible,
            decision.Notice is not null,
            decision.Notice ?? "",
            decision.ShowWattValue ? wattLabel : WattUnavailableLabel,
            decision.Diagnostic,
            decision.GpuPowerEnabled || decision.GpuBoostEnabled || decision.GpuTempEnabled);
    }

    public static string Diagnostic(
        string build,
        Decision decision,
        GpuPowerCapability.Result power,
        XgDockSnapshot snap,
        bool admissionClosed,
        bool gpuEco,
        string? initFault)
    {
        string page = decision.PageVisible ? "visible" : "hidden";
        string slider = (decision.GpuPowerVisible ? "visible" : "hidden")
            + "/"
            + (decision.GpuPowerEnabled ? "enabled" : "disabled");
        string range = power.RangeEstablished
            ? $"{power.RangeSource} max={power.RangeLimit} additional={power.AdditionalMaximum} measured={(power.MeasuredMaxLimit?.ToString() ?? "none")}"
            : power.MeasuredMaxLimit is int measured ? $"unavailable measured={measured}" : "unavailable";
        return "GPU power UI"
            + " build=" + build
            + " page=" + page
            + " slider=" + slider
            + " reason=" + decision.Reason
            + " base=" + power.BaseRead
            + " power=" + power.PowerRead
            + " powerResponds=" + (power.PowerResponds ? "1" : "0")
            + " range=" + range
            + " dock=" + snap.State
            + " sample=" + (snap.HasSample ? "1" : "0")
            + " admission=" + (admissionClosed ? "closed" : "open")
            + " eco=" + (gpuEco ? "1" : "0")
            + " init=" + (string.IsNullOrEmpty(initFault) ? "none" : initFault);
    }
}
