using Xunit;

public class GpuLimitTests
{
    sealed class Script
    {
        public Queue<XgDockSnapshot> Next { get; } = new();
        public XgDockSnapshot Read() => Next.Dequeue();
    }

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

    static XgDockSnapshot Active() => Reads(Value(1), Value(1));
    static XgDockSnapshot Inactive() => Reads(Value(1), Value(0));
    static XgDockSnapshot Failed() => Reads(TransportFailed(), TransportFailed());
    static XgDockSnapshot ShortRead() => Reads(Short(), Short());
    static XgDockSnapshot UnknownValue() => Reads(Value(1), Value(0x101));

    static (XgMitigation Guard, Script Script) NewGuard(params XgDockSnapshot[] queued)
    {
        var script = new Script();
        foreach (var item in queued) script.Next.Enqueue(item);
        var guard = new XgMitigation { Write = _ => { } };
        guard.Enable(script.Read);
        return (guard, script);
    }

    static XgMitigation.GpuLimitApplyResult Apply(XgMitigation guard, List<(uint Device, int Value)> writes, bool explicitApply = false, int boostResult = 1)
    {
        XgMitigation.GpuLimitApplyResult result;
        Func<uint, int, string, int> write = (device, value, _) =>
        {
            writes.Add((device, value));
            return device == (uint)AsusACPI.PPT_GPUC0 ? boostResult : 1;
        };
        if (explicitApply)
        {
            using (XgMitigation.Explicit(XgOp.Power | XgOp.GpuThermal))
                result = guard.ApplySupportedGpuLimits(true, 25, true, 5, true, 75, write);
        }
        else
        {
            result = guard.ApplySupportedGpuLimits(true, 25, true, 5, true, 75, write);
        }
        return result;
    }

    [Fact]
    public void ConfirmedInactive_PermitsSupportedGpuChanges()
    {
        var (guard, _) = NewGuard(Inactive(), Inactive());
        var writes = new List<(uint Device, int Value)>();
        var result = Apply(guard, writes);
        Assert.False(result.Suppressed);
        Assert.Equal(3, result.Writes);
        Assert.Contains(writes, item => item.Device == AsusACPI.GPU_POWER && item.Value == 25);
        Assert.Contains(writes, item => item.Device == (uint)AsusACPI.PPT_GPUC0 && item.Value == 5);
        Assert.Contains(writes, item => item.Device == (uint)AsusACPI.PPT_GPUC2 && item.Value == 75);
    }

    [Fact]
    public void UnsupportedLimits_AreNotWritten()
    {
        var (guard, _) = NewGuard(Inactive());
        var writes = new List<(uint Device, int Value)>();
        var result = guard.ApplySupportedGpuLimits(false, 25, true, 5, false, 75, (device, value, _) =>
        {
            writes.Add((device, value));
            return 1;
        });
        Assert.False(result.Suppressed);
        Assert.Equal(new[] { (uint)AsusACPI.PPT_GPUC0 }, writes.Select(item => item.Device).ToArray());
    }

    [Theory]
    [InlineData("active")]
    [InlineData("unknown")]
    [InlineData("failed")]
    [InlineData("short")]
    public void ClosedDockStates_SuppressGpuLimits(string state)
    {
        XgDockSnapshot snap = state switch
        {
            "active" => Active(),
            "unknown" => UnknownValue(),
            "failed" => Failed(),
            "short" => ShortRead(),
            _ => throw new ArgumentOutOfRangeException(nameof(state))
        };
        var (guard, _) = NewGuard(snap, snap);
        var writes = new List<(uint Device, int Value)>();
        var result = Apply(guard, writes, explicitApply: true);
        Assert.True(result.Suppressed);
        Assert.Empty(writes);
        var shown = guard.PresentGpuLimits(true, true, true);
        Assert.False(shown.Enabled);
        Assert.Equal(XgMitigation.GpuLimitsLockedText, shown.Explanation);
    }

    [Fact]
    public void Transition_SuppressesGpuLimits()
    {
        var (guard, _) = NewGuard();
        guard.EnterTransition("activate");
        var writes = new List<(uint Device, int Value)>();
        var result = Apply(guard, writes, explicitApply: true);
        Assert.True(result.Suppressed);
        Assert.Empty(writes);
        Assert.False(guard.GpuLimitsOpen());
        guard.ExitTransition("activate");
    }

    [Fact]
    public void ActivationBetweenSliderAndExecution_PreventsTheWrite()
    {
        var (guard, _) = NewGuard(Inactive());
        Assert.True(guard.GpuLimitsOpen());
        guard.EnterTransition("activate");
        var writes = new List<(uint Device, int Value)>();
        var result = Apply(guard, writes, explicitApply: true);
        Assert.True(result.Suppressed);
        Assert.Empty(writes);
        guard.ExitTransition("activate");
    }

    [Fact]
    public void PendingTransition_SuppressesGpuLimits()
    {
        var (guard, _) = NewGuard(Inactive());
        var waiting = new ManualResetEventSlim();
        guard.TransitionWaiting = waiting;
        Task transition;
        using (var held = guard.Enter("slider", "DEVS", XgOp.Power))
        {
            Assert.True(held.Allowed);
            transition = Task.Run(() => guard.EnterTransition("activate"));
            Assert.True(waiting.Wait(TimeSpan.FromSeconds(2)));
            var writes = new List<(uint Device, int Value)>();
            XgMitigation.GpuLimitApplyResult result = default;
            var apply = Task.Run(() => result = Apply(guard, writes, explicitApply: true));
            Assert.True(apply.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(result.Suppressed);
            Assert.Empty(writes);
        }
        Assert.True(transition.Wait(TimeSpan.FromSeconds(2)));
        guard.ExitTransition("activate");
    }

    [Fact]
    public void OpeningOrRefreshing_SendsNoHardwareWrites()
    {
        var (guard, _) = NewGuard(Inactive(), Active(), Failed());
        var writes = new List<(uint Device, int Value)>();
        var opened = guard.PresentGpuLimits(true, true, false);
        var refreshed = guard.PresentGpuLimits(true, true, true);
        var failed = guard.PresentGpuLimits(false, true, false);
        Assert.True(opened.Enabled);
        Assert.Null(opened.Explanation);
        Assert.False(refreshed.Enabled);
        Assert.Equal(XgMitigation.GpuLimitsLockedText, refreshed.Explanation);
        Assert.False(failed.Enabled);
        Assert.Empty(writes);
    }

    [Fact]
    public void Ejection_DoesNotReplayBlockedGpuLimits()
    {
        var (guard, _) = NewGuard(Active(), Active(), Inactive(), Inactive(), Inactive(), Inactive());
        var writes = new List<(uint Device, int Value)>();
        var blocked = Apply(guard, writes, explicitApply: true);
        Assert.True(blocked.Suppressed);
        Assert.True(guard.AwaitingExplicit);
        var replay = Apply(guard, writes);
        Assert.True(replay.Suppressed);
        Assert.Empty(writes);

        var explicitApply = Apply(guard, writes, explicitApply: true);
        Assert.False(explicitApply.Suppressed);
        Assert.Equal(3, writes.Count);
    }
}
