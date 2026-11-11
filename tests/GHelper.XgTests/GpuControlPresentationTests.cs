using GHelper.Mode;
using Xunit;

public class GpuControlPresentationTests
{
    static DecodedRead Value(int decoded, bool ok = true, uint bytes = 16)
    {
        var buffer = new byte[Math.Max(4, bytes)];
        BitConverter.GetBytes(decoded + 65536).CopyTo(buffer, 0);
        return new DecodedRead
        {
            IoctlSucceeded = ok,
            BytesReturned = bytes,
            Win32Error = ok ? 0 : 6,
            Buffer = buffer
        };
    }

    static DecodedRead TransportFailed() => new()
    {
        IoctlSucceeded = false,
        BytesReturned = 16,
        Win32Error = 6,
        Buffer = new byte[16]
    };

    static DecodedRead Short() => new()
    {
        IoctlSucceeded = true,
        BytesReturned = 2,
        Buffer = new byte[2]
    };

    static XgDockSnapshot Reads(DecodedRead connected, DecodedRead activation)
        => XgDockClassifier.FromReads(connected, activation);

    static XgDockSnapshot ConnectedInactive() => Reads(Value(1), Value(0));
    static XgDockSnapshot Disconnected() => Reads(Value(0), Value(0));
    static XgDockSnapshot Active() => Reads(Value(1), Value(1));
    static XgDockSnapshot Failed() => Reads(TransportFailed(), TransportFailed());
    static XgDockSnapshot ShortRead() => Reads(Short(), Short());
    static XgDockSnapshot UnknownValue() => Reads(Value(1), Value(0x101));

    static XgDockSnapshot Transitioning() => new()
    {
        State = XgDockState.Transitioning,
        Connected = Value(1),
        Activation = Value(1),
        HasSample = true,
        Source = "transition_flag"
    };

    static GpuPowerCapability.Result Probe(DecodedRead gpuBase, DecodedRead? gpuPower = null, int? measured = null, int? fallback = null)
        => GpuPowerCapability.Evaluate(gpuBase, gpuPower ?? default, measured, fallback);

    static GpuControlPresentation.Decision Present(
        XgDockSnapshot snap,
        DecodedRead gpuBase,
        DecodedRead? gpuPower = null,
        int? measured = null,
        bool nvidia = false,
        bool boost = false,
        bool temp = false,
        bool clocks = false,
        bool admissionClosed = false,
        bool eco = false,
        string? initFault = null,
        int? fallback = 175)
        => GpuControlPresentation.Decide(
            snap, admissionClosed, true, nvidia,
            Probe(gpuBase, gpuPower, measured, fallback),
            boost, temp, clocks, eco, initFault, "test-build");

    static GpuControlPresentation.BoundControls Wire(GpuControlPresentation.Decision decision, string wattLabel = "105W")
        => GpuControlPresentation.Bind(decision, wattLabel);

    [Fact]
    public void MeasuredRange_IsTheOnlyEstablishedRange()
    {
        var valid = Probe(Value(100), Value(20), 150);
        Assert.True(valid.RangeEstablished);
        Assert.Equal(150 - 100 - AsusACPI.MaxGPUBoost, valid.AdditionalMaximum);
        Assert.Equal("nvidia-smi", valid.RangeSource);
        Assert.Equal("nvidia-smi", Probe(Value(100), Value(20), 150, 175).RangeSource);

        var fallback = Probe(Value(100), Value(20), null, 175);
        Assert.True(fallback.RangeEstablished);
        Assert.Equal("model-fallback", fallback.RangeSource);
        Assert.Equal(175 - 100 - AsusACPI.MaxGPUBoost, fallback.AdditionalMaximum);
        var missing = Probe(Value(100), Value(20), null, null);
        Assert.True(missing.BasePositive);
        Assert.True(missing.PowerResponds);
        Assert.False(missing.RangeEstablished);
        Assert.Null(missing.AdditionalMaximum);
        Assert.False(Probe(Value(0), Value(20), 150).BasePositive);
        Assert.False(Probe(Value(-1), Value(20), 150).BasePositive);
        Assert.False(Probe(TransportFailed(), Value(20), 150).BasePositive);
        Assert.False(Probe(Short(), Value(20), 150).BasePositive);
        Assert.False(Probe(Value(100), TransportFailed(), 150).PowerResponds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ConnectedInactive_KeepsThePowerRowWhenGpuBaseIsNotPositive(int decoded)
    {
        var decision = Present(ConnectedInactive(), Value(decoded), Value(20), 150, boost: true, temp: true);
        var bound = Wire(decision);
        Assert.True(decision.PageVisible);
        Assert.True(decision.CpuTdpEnabled);
        Assert.True(bound.PowerRowVisible);
        Assert.False(bound.PowerEnabled);
        Assert.False(decision.ShowWattValue);
        Assert.Equal(GpuControlPresentation.WattUnavailableLabel, bound.PowerLabel);
        Assert.Equal(GpuControlPresentation.CapabilityUnavailableText, bound.Notice);
        Assert.Equal("gpu_base_unavailable", decision.Reason);
        Assert.True(bound.BoostEnabled);
        Assert.True(bound.ApplyLimitsEnabled);
        Assert.Contains("slider=visible/disabled", decision.Diagnostic);
        Assert.Contains("range=unavailable", decision.Diagnostic);
        Assert.Contains("decoded=" + decoded, decision.Diagnostic);
        Assert.Contains("build=test-build", decision.Diagnostic);
        Assert.Contains("dock=Inactive", decision.Diagnostic);
        Assert.DoesNotContain("175", bound.PowerLabel);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("short")]
    public void ConnectedInactive_KeepsThePowerRowWhenGpuBaseTransportFails(string kind)
    {
        DecodedRead gpuBase = kind == "failed" ? TransportFailed() : Short();
        var first = Present(ConnectedInactive(), gpuBase);
        var refresh = Present(ConnectedInactive(), gpuBase);
        var bound = Wire(refresh);
        Assert.True(first.GpuPowerVisible);
        Assert.True(bound.PowerRowVisible);
        Assert.False(bound.PowerEnabled);
        Assert.Equal(GpuControlPresentation.WattUnavailableLabel, bound.PowerLabel);
        Assert.Equal(GpuControlPresentation.GpuCapabilityUnavailableText, bound.Notice);
        Assert.Equal("gpu_base_unavailable", refresh.Reason);
        Assert.Contains("range=unavailable", refresh.Diagnostic);
        Assert.False(Probe(gpuBase, fallback: 175).RangeEstablished);
        Assert.Null(Probe(gpuBase, fallback: 175).AdditionalMaximum);
        Assert.Contains(kind == "failed" ? "ioctl=fail" : "bytes=2", refresh.Diagnostic);
        Assert.Contains("powerResponds=0", refresh.Diagnostic);
    }

    [Fact]
    public void ConnectedInactive_WithoutNvidia_EnablesOnlyAMeasuredRange()
    {
        var decision = Present(ConnectedInactive(), Value(100), Value(20), 150, boost: true, temp: true);
        var bound = Wire(decision, "120W");
        Assert.False(decision.ClockOffsetVisible);
        Assert.False(decision.ClockLimitVisible);
        Assert.True(bound.PowerRowVisible);
        Assert.True(bound.PowerEnabled);
        Assert.True(decision.CpuTdpEnabled);
        Assert.Equal("120W", bound.PowerLabel);
        Assert.Equal("editable", decision.Reason);
        Assert.True(bound.ApplyLimitsEnabled);
        Assert.False(bound.NoticeVisible);
        Assert.Contains("slider=visible/enabled", decision.Diagnostic);
        Assert.Contains("range=nvidia-smi max=150 additional=" + (150 - 100 - AsusACPI.MaxGPUBoost), decision.Diagnostic);
        Assert.Contains("powerResponds=1", decision.Diagnostic);
        Assert.Contains("ioctl=ok", decision.Diagnostic);
    }

    [Fact]
    public void MissingMeasurement_UsesTheModelFallbackAndShowsWatts()
    {
        var decision = Present(ConnectedInactive(), Value(100), Value(20), null);
        var bound = Wire(decision, "150W");
        Assert.True(bound.PowerRowVisible);
        Assert.True(bound.PowerEnabled);
        Assert.False(bound.NoticeVisible);
        Assert.Equal("150W", bound.PowerLabel);
        Assert.Equal("editable", decision.Reason);
        Assert.Contains("range=model-fallback max=175 additional=" + (175 - 100 - AsusACPI.MaxGPUBoost), decision.Diagnostic);
        Assert.Contains("measured=none", decision.Diagnostic);

        var noFallback = Present(ConnectedInactive(), Value(100), Value(20), null, fallback: null);
        Assert.False(noFallback.ShowWattValue);
        Assert.Equal(GpuControlPresentation.WattUnavailableLabel, Wire(noFallback, "175W").PowerLabel);
        Assert.Equal("range_unavailable", noFallback.Reason);
    }

    [Fact]
    public void GpuPowerReadFailure_DoesNotAcceptAWattEdit()
    {
        var decision = Present(ConnectedInactive(), Value(100), TransportFailed(), null, boost: true, temp: true);
        var bound = Wire(decision, "50W");
        Assert.True(bound.PowerRowVisible);
        Assert.False(bound.PowerEnabled);
        Assert.False(decision.ShowWattValue);
        Assert.Equal(GpuControlPresentation.WattUnavailableLabel, bound.PowerLabel);
        Assert.Equal("gpu_power_unavailable", decision.Reason);
        Assert.Equal(GpuControlPresentation.GpuPowerUnavailableText, bound.Notice);
        Assert.True(decision.CpuTdpEnabled);
        Assert.True(bound.BoostEnabled);
        Assert.True(bound.TempEnabled);
        Assert.Contains("powerResponds=0", decision.Diagnostic);
    }

    [Fact]
    public void ActiveAndTransitioning_LockTheVisibleRow()
    {
        foreach (var snap in new[] { Active(), Transitioning() })
        {
            var valid = Present(snap, Value(100), Value(20), 150, nvidia: false);
            var failedBase = Present(snap, Value(0));
            Assert.True(Wire(valid, "120W").PowerRowVisible);
            Assert.False(Wire(valid).PowerEnabled);
            Assert.Equal(XgMitigation.GpuLimitsLockedText, valid.Notice);
            Assert.Equal("locked", valid.Reason);
            Assert.Equal("120W", Wire(valid, "120W").PowerLabel);
            Assert.True(Wire(failedBase).PowerRowVisible);
            Assert.False(Wire(failedBase).PowerEnabled);
            Assert.Equal(GpuControlPresentation.WattUnavailableLabel, Wire(failedBase, "105W").PowerLabel);
            Assert.False(Wire(valid).ApplyLimitsEnabled);
            Assert.False(Wire(failedBase).ApplyLimitsEnabled);
            Assert.Equal("locked", failedBase.Reason);
            Assert.Contains("dock=" + snap.State, failedBase.Diagnostic);
        }
    }

    [Fact]
    public void InitFault_DoesNotHideAConnectedPowerRow()
    {
        var decision = Present(ConnectedInactive(), Value(0), initFault: "InvalidOperationException");
        var bound = Wire(decision);
        Assert.True(bound.PageVisible);
        Assert.True(bound.PowerRowVisible);
        Assert.False(bound.PowerEnabled);
        Assert.Equal(GpuControlPresentation.WattUnavailableLabel, bound.PowerLabel);
        Assert.Contains("init=InvalidOperationException", decision.Diagnostic);
        Assert.Equal("gpu_base_unavailable", decision.Reason);
    }

    [Fact]
    public void Refresh_KeepsTheConnectedPowerRowVisible()
    {
        var open = Wire(Present(ConnectedInactive(), Value(0)));
        var afterInit = Wire(Present(ConnectedInactive(), TransportFailed()));
        var afterLockRefresh = Wire(Present(Active(), Value(0)));
        Assert.True(open.PowerRowVisible);
        Assert.True(afterInit.PowerRowVisible);
        Assert.True(afterLockRefresh.PowerRowVisible);
        Assert.False(afterLockRefresh.PowerEnabled);
    }

    [Fact]
    public void EcoOnAConnectedDock_DoesNotHideThePageOrPowerRow()
    {
        var connected = Present(ConnectedInactive(), Value(0), eco: true);
        Assert.True(connected.PageVisible);
        Assert.True(Wire(connected).PowerRowVisible);
        var disconnected = Present(Disconnected(), Value(100), Value(20), 150, nvidia: true, eco: true);
        Assert.False(disconnected.PageVisible);
        Assert.False(Wire(disconnected).PowerRowVisible);
        Assert.Equal("page_hidden", disconnected.Reason);
    }

    [Fact]
    public void Disconnected_HidesTheGpuPage_AndLeavesCpuTdpAvailable()
    {
        var shown = Present(Disconnected(), Value(100), Value(20), 150);
        Assert.False(shown.PageVisible);
        Assert.True(shown.CpuTdpEnabled);
        Assert.False(Wire(shown).PowerRowVisible);
        Assert.Null(shown.Notice);
    }

    [Theory]
    [InlineData("transition")]
    [InlineData("unknown")]
    [InlineData("failed")]
    [InlineData("short")]
    [InlineData("pending")]
    public void ClosedStates_BlockTheSlider(string state)
    {
        XgDockSnapshot snap = state switch
        {
            "transition" => Transitioning(),
            "unknown" => UnknownValue(),
            "failed" => Failed(),
            "short" => ShortRead(),
            "pending" => ConnectedInactive(),
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
        bool connected = state is "transition" or "unknown" or "pending";
        var shown = Present(snap, Value(0), boost: true, temp: true, admissionClosed: state is "transition" or "pending");
        var bound = Wire(shown);
        Assert.Equal(connected, shown.PageVisible);
        Assert.Equal(connected, bound.PowerRowVisible);
        Assert.False(shown.CpuTdpEnabled);
        Assert.False(bound.PowerEnabled);
        Assert.False(bound.BoostEnabled);
        Assert.False(bound.TempEnabled);
        if (shown.PageVisible)
            Assert.Equal(XgMitigation.GpuLimitsLockedText, shown.Notice);
    }

    [Fact]
    public void VisibilityDecision_DoesNotItselfWrite()
    {
        var writes = 0;
        var shown = Present(ConnectedInactive(), Value(100), Value(20), 150);
        Assert.True(shown.GpuPowerEnabled);
        Assert.Equal(0, writes);
    }

    [Fact]
    public void UnrequestedGpuPower_IsNotWritten()
    {
        var script = new Queue<XgDockSnapshot>();
        script.Enqueue(ConnectedInactive());
        var guard = new XgMitigation { Write = _ => { } };
        guard.Enable(() => script.Dequeue());
        var writes = new List<uint>();
        var result = guard.ApplySupportedGpuLimits(false, 10, true, 5, false, 80, (device, _, _) =>
        {
            writes.Add(device);
            return 1;
        });
        Assert.False(result.Suppressed);
        Assert.DoesNotContain(AsusACPI.GPU_POWER, writes);
        Assert.Contains((uint)AsusACPI.PPT_GPUC0, writes);
    }
}
