using Xunit;

public class XgMitigationTests
{
    sealed class Script
    {
        public Queue<XgDockSnapshot> Next { get; } = new();
        public int Reads;
        public XgDockSnapshot Read()
        {
            Reads++;
            return Next.Dequeue();
        }
    }

    static DecodedRead Value(int decoded, bool ok = true, uint bytes = 16, int win32 = 0)
    {
        var buffer = new byte[16];
        BitConverter.GetBytes(decoded + 65536).CopyTo(buffer, 0);
        return new DecodedRead
        {
            IoctlSucceeded = ok,
            BytesReturned = bytes,
            Win32Error = win32,
            Buffer = buffer
        };
    }

    static DecodedRead TransportFailed(int decodedInBuffer)
    {
        var buffer = new byte[16];
        BitConverter.GetBytes(decodedInBuffer + 65536).CopyTo(buffer, 0);
        return new DecodedRead
        {
            IoctlSucceeded = false,
            BytesReturned = 16,
            Win32Error = 6,
            Buffer = buffer
        };
    }

    static XgDockSnapshot Reads(DecodedRead connected, DecodedRead activation)
        => XgDockClassifier.FromReads(connected, activation);

    static XgDockSnapshot Active() => Reads(Value(1), Value(1));
    static XgDockSnapshot Inactive() => Reads(Value(1), Value(0));
    static XgDockSnapshot Failed() => Reads(TransportFailed(0), TransportFailed(0));

    static (XgMitigation Guard, Script Script, List<string> Log) NewGuard(params XgDockSnapshot[] queued)
    {
        var script = new Script();
        foreach (var item in queued) script.Next.Enqueue(item);
        var log = new List<string>();
        var guard = new XgMitigation { Write = log.Add };
        guard.Enable(script.Read);
        return (guard, script, log);
    }

    static int Writes;

    static int Try(XgMitigation guard, uint device, int value, bool explicitApply = false)
    {
        int result;
        if (explicitApply)
        {
            using (XgMitigation.Explicit(XgMitigation.OperationForDevice(device)))
                result = guard.RunGuarded("test", device, value.ToString(), () =>
                {
                    Writes++;
                    return new DevsOutcome { Result = 1, IoctlSucceeded = true, BytesReturned = 16 };
                });
        }
        else
        {
            result = guard.RunGuarded("test", device, value.ToString(), () =>
            {
                Writes++;
                return new DevsOutcome { Result = 1, IoctlSucceeded = true, BytesReturned = 16 };
            });
        }
        return result;
    }

    public XgMitigationTests() => Writes = 0;

    [Fact]
    public void ActiveDock_BlocksGuardedMutations()
    {
        var (guard, _, _) = NewGuard(Active());
        int result = Try(guard, (uint)AsusACPI.PPT_APUA3, 15);
        Assert.Equal(XgMitigation.Suppressed, result);
        Assert.Equal(0, Writes);
    }

    [Fact]
    public void FailedOrUnknownRead_BlocksGuardedMutations()
    {
        var failed = NewGuard(Failed());
        Assert.Equal(XgMitigation.Suppressed, Try(failed.Guard, AsusACPI.GPU_POWER, 25));
        Assert.Equal(0, Writes);

        var other = NewGuard(Reads(Value(1), Value(0x101)));
        Assert.Equal(XgDockState.Unknown, other.Script.Next.Peek().State);
        Assert.Equal(XgMitigation.Suppressed, Try(other.Guard, (uint)AsusACPI.PPT_GPUC2, 75));
        Assert.Equal(0, Writes);
    }

    [Fact]
    public void InitialFailedDetection_CanRecover_AndExplicitApplyWorks()
    {
        var (guard, _, _) = NewGuard(Failed(), Inactive(), Inactive(), Inactive());
        Assert.Equal(XgMitigation.Suppressed, Try(guard, (uint)AsusACPI.PPT_APUA0, 15));
        Assert.False(guard.ControlsLocked());
        Assert.Equal(XgMitigation.Suppressed, Try(guard, (uint)AsusACPI.PPT_APUA0, 15));
        Assert.Equal(0, Writes);
        Assert.Equal(1, Try(guard, (uint)AsusACPI.PPT_APUA0, 15, explicitApply: true));
        Assert.Equal(1, Writes);
    }

    [Fact]
    public void ReliablyInactive_AllowsAutomaticWhenNothingWasBlocked()
    {
        var (guard, _, _) = NewGuard(Inactive());
        Assert.Equal(1, Try(guard, (uint)AsusACPI.PPT_APUA0, 30));
        Assert.Equal(1, Writes);
        Assert.False(guard.AwaitingExplicit);
    }

    [Fact]
    public void Transition_BlocksGuardedMutations_LifecycleStillRuns()
    {
        var (guard, script, _) = NewGuard();
        guard.EnterTransition("test");
        Assert.Equal(0, script.Reads);
        Assert.Equal(XgMitigation.Suppressed, Try(guard, AsusACPI.PerformanceMode, 1));
        Assert.Equal(0, Writes);

        int lifecycle = 0;
        int result = guard.RunLifecycle("GPU XGM", AsusACPI.GPUXG, "1", () =>
        {
            lifecycle++;
            return new DevsOutcome { Result = 1, IoctlSucceeded = true, BytesReturned = 16 };
        });
        Assert.Equal(1, result);
        Assert.Equal(1, lifecycle);

        guard.ExitTransition("test");
        Assert.Equal(0, Writes);
    }

    [Fact]
    public void CpuBoost_RemainsAvailableWithoutReadingDock()
    {
        var (guard, script, log) = NewGuard(Active());
        int boosts = 0;
        guard.RunCpuBoost("ComboBoost", () => boosts++);
        Assert.Equal(1, boosts);
        Assert.Equal(0, script.Reads);
        Assert.Contains("api=PowerWriteACValueIndex", string.Join("\n", log));
        Assert.Contains("action=completed", string.Join("\n", log));
        Assert.DoesNotContain("api=DEVS", string.Join("\n", log));
    }

    [Fact]
    public void GpuXgLifecycle_RemainsAvailableWhileActive()
    {
        var (guard, _, _) = NewGuard(Active());
        Assert.False(XgMitigation.IsGuardedDevice(AsusACPI.GPUXG));
        int calls = 0;
        guard.RunLifecycle("GPU XGM", AsusACPI.GPUXG, "257", () =>
        {
            calls++;
            return new DevsOutcome { Result = 1, IoctlSucceeded = true, BytesReturned = 16 };
        });
        Assert.Equal(1, calls);
    }

    [Fact]
    public void FanReset_AndSmuThermal_CannotBypass()
    {
        var (guard, _, _) = NewGuard(Active(), Active(), Active());
        Assert.True(XgMitigation.IsGuardedDevice(AsusACPI.PerformanceMode));
        Assert.True(XgMitigation.IsGuardedDevice(AsusACPI.DevsCPUFanCurve));
        Assert.Equal(XgMitigation.Suppressed, Try(guard, AsusACPI.PerformanceMode, AsusACPI.PerformanceTurbo));
        Assert.Equal(XgMitigation.Suppressed, Try(guard, AsusACPI.DevsCPUFanCurve, 0));

        bool smu = false;
        Assert.False(guard.TryMutate("SetCPUTemp", "SMU.SetThm", "95", XgOp.Thermal, () => smu = true));
        Assert.False(smu);
        Assert.Equal(0, Writes);
    }

    [Fact]
    public void NoDeferredWriteOnEject()
    {
        var (guard, script, _) = NewGuard(Active());
        Assert.Equal(XgMitigation.Suppressed, Try(guard, (uint)AsusACPI.PPT_APUA3, 15));
        script.Next.Enqueue(Inactive());
        Assert.Equal(0, Writes);
        Assert.True(guard.AwaitingExplicit);
        Assert.Equal(XgMitigation.Suppressed, Try(guard, (uint)AsusACPI.PPT_APUA3, 15));
        Assert.Equal(0, Writes);
    }

    [Fact]
    public void UnrelatedDevice_IsNotBlocked()
    {
        var (guard, _, _) = NewGuard(Active());
        Assert.False(XgMitigation.IsGuardedDevice(AsusACPI.UniversalControl));
        Assert.Equal(1, Try(guard, AsusACPI.UniversalControl, 1));
        Assert.Equal(1, Writes);
    }

    [Fact]
    public void Classifier_DoesNotTreatCommandValueAsReadback()
    {
        Assert.Equal(XgValueClass.Other, XgDockClassifier.Classify(Value(0x101)));
        Assert.Equal(XgValueClass.One, XgDockClassifier.Classify(Value(1)));
        Assert.Equal(XgValueClass.Zero, XgDockClassifier.Classify(Value(0)));
        Assert.Equal(XgDockState.Unknown, Reads(Value(1), Value(0x101)).State);
        Assert.Equal(XgDockState.Active, Reads(Value(1), Value(1)).State);
        Assert.Equal(XgDockState.Inactive, Reads(Value(0), Value(0)).State);
        Assert.Equal(XgDockState.Inactive, Reads(Value(1), Value(0)).State);
    }

    [Fact]
    public void TransportFailure_IsNotInactive_EvenIfBufferLooksLikeZero()
    {
        Assert.Equal(XgValueClass.TransportFailed, XgDockClassifier.Classify(TransportFailed(0)));
        Assert.Equal(XgDockState.Unknown, Reads(TransportFailed(0), Value(0)).State);
        var shortRead = Value(0, ok: true, bytes: 2);
        Assert.Equal(XgValueClass.TransportFailed, XgDockClassifier.Classify(shortRead));
        var staleError = Value(0, ok: true, win32: 6);
        Assert.Equal(XgValueClass.Zero, XgDockClassifier.Classify(staleError));
    }

    [Fact]
    public async Task InFlightMutation_DelaysTransitionUntilItFinishes()
    {
        var (guard, script, _) = NewGuard(Inactive());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var worker = Task.Run(() =>
        {
            guard.RunGuarded("SetPower", (uint)AsusACPI.PPT_APUA0, "15", () =>
            {
                started.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                lock (order) order.Add("write-finished");
                return new DevsOutcome { Result = 1, IoctlSucceeded = true, BytesReturned = 16 };
            });
        });

        Assert.Same(started.Task, await Task.WhenAny(started.Task, Task.Delay(2000)));
        var transition = Task.Run(() =>
        {
            guard.EnterTransition("GPUXG activate");
            lock (order) order.Add("transition");
        });
        Assert.True(SpinWait.SpinUntil(() => guard.TransitionWaits > 0, 2000));
        Assert.NotSame(transition, await Task.WhenAny(transition, Task.Delay(100)));
        release.TrySetResult();
        Assert.Same(transition, await Task.WhenAny(transition, Task.Delay(2000)));
        await worker.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { "write-finished", "transition" }, order);
        Assert.True(guard.InTransition);
        guard.ExitTransition("GPUXG activate");
        Assert.Equal(1, script.Reads);
    }

    [Fact]
    public void TemperatureTrace_DistinguishesSources_AndKeepsPriorSamples()
    {
        var lines = new List<string>();
        var trace = new XgTempTrace { Write = lines.Add, MonotonicMs = () => 1000, UtcNow = () => DateTimeOffset.Parse("2026-10-26T12:00:00Z") };
        trace.Record(Value(40), TempOrigin.FreshAsus, 40);
        int afterFresh = lines.Count;
        trace.RecordCached(40);
        Assert.Equal(afterFresh, lines.Count);

        trace.Record(Value(106), TempOrigin.FreshAsus, 106);
        string joined = string.Join("\n", lines);
        Assert.Contains("kind=history", joined);
        Assert.Contains("origin=FreshAsus", joined);
        Assert.Contains("decoded=40", joined);
        Assert.Contains("decoded=106", joined);
        Assert.Contains("transport=ok", joined);

        lines.Clear();
        trace.Record(TransportFailed(106), TempOrigin.ThermalZone, 50);
        joined = string.Join("\n", lines);
        Assert.Contains("origin=ThermalZone", joined);
        Assert.Contains("transport=fail", joined);

        lines.Clear();
        trace.Record(TransportFailed(-1), TempOrigin.Failed, null);
        Assert.True(lines.Exists(line => line.Contains("kind=sample") && line.Contains("origin=Failed") && line.Contains("transport=fail")));

        lines.Clear();
        trace.Record(Value(106), TempOrigin.FreshAsus, 106);
        string secondHot = string.Join("\n", lines);
        Assert.Contains("kind=sample", secondHot);
        Assert.DoesNotContain("kind=history", secondHot);
    }

    [Fact]
    public void ExplicitPower_DoesNotResumeThermalFanOrUv()
    {
        var (guard, _, _) = NewGuard(Active(), Inactive(), Inactive(), Inactive(), Inactive(), Inactive(), Inactive());
        Assert.Equal(XgMitigation.Suppressed, Try(guard, (uint)AsusACPI.PPT_APUA3, 15));
        Assert.Equal(1, Try(guard, (uint)AsusACPI.PPT_APUA0, 20, explicitApply: true));
        Assert.True(guard.AwaitingExplicit);
        Assert.Equal(XgMitigation.Suppressed, Try(guard, (uint)AsusACPI.PPT_TEMP9E, 90));
        Assert.Equal(XgMitigation.Suppressed, Try(guard, AsusACPI.DevsCPUFanCurve, 1));
        bool uv = false;
        Assert.False(guard.TryMutate("SetUV", "SMU.SetCoAll", "0", XgOp.Uv, () => uv = true));
        Assert.False(uv);

        using (XgMitigation.Explicit(XgOp.CpuLimit))
            Assert.Equal(1, guard.RunGuarded("limit", (uint)AsusACPI.PPT_TEMP9E, "90", () =>
            {
                Writes++;
                return new DevsOutcome { Result = 1, IoctlSucceeded = true, BytesReturned = 16 };
            }));
        Assert.True(guard.AwaitingExplicit);
        Assert.Equal(XgMitigation.Suppressed, Try(guard, (uint)AsusACPI.PPT_APUA3, 25));
    }

    [Fact]
    public async Task ExplicitPower_FlowsOnlyToThatCall()
    {
        var (guard, _, _) = NewGuard(Active(), Inactive(), Inactive(), Inactive());
        Assert.Equal(XgMitigation.Suppressed, Try(guard, (uint)AsusACPI.PPT_APUA3, 15));

        int thermal;
        using (XgMitigation.Explicit(XgOp.Power))
        {
            var power = Task.Run(() => Try(guard, (uint)AsusACPI.PPT_APUA0, 20));
            var blocked = Task.Run(() => Try(guard, (uint)AsusACPI.PPT_TEMP9E, 90));
            Assert.Equal(1, await power);
            thermal = await blocked;
        }

        Assert.Equal(XgMitigation.Suppressed, thermal);
        Assert.Equal(XgMitigation.Suppressed, Try(guard, (uint)AsusACPI.PPT_TEMP9E, 90));
    }

    [Fact]
    public void ExplicitAction_StaysBlockedWhileActiveUnknownOrTransitioning()
    {
        var active = NewGuard(Active());
        Assert.Equal(XgMitigation.Suppressed, Try(active.Guard, (uint)AsusACPI.PPT_APUA0, 15, explicitApply: true));

        var unknown = NewGuard(Reads(Value(2), Value(0)));
        Assert.Equal(XgDockState.Unknown, XgDockClassifier.FromReads(Value(2), Value(0)).State);
        Assert.Equal(XgMitigation.Suppressed, Try(unknown.Guard, (uint)AsusACPI.PPT_APUA0, 15, explicitApply: true));

        var contradictory = NewGuard(Reads(Value(0), Value(1)));
        Assert.Equal(XgDockState.Unknown, Reads(Value(0), Value(1)).State);
        Assert.Equal(XgMitigation.Suppressed, Try(contradictory.Guard, (uint)AsusACPI.PPT_APUA0, 15, explicitApply: true));

        var (guard, _, _) = NewGuard();
        guard.EnterTransition("activate");
        Assert.Equal(XgMitigation.Suppressed, Try(guard, (uint)AsusACPI.PPT_APUA0, 15, explicitApply: true));
        guard.ExitTransition("activate");
        Assert.Equal(0, Writes);
    }

    [Fact]
    public void DockTruthTable_AcceptsOnlyEstablishedPairs()
    {
        XgValueClass[] classes = { XgValueClass.Zero, XgValueClass.One, XgValueClass.Other, XgValueClass.TransportFailed };
        foreach (var connected in classes)
        foreach (var activation in classes)
        {
            var state = XgDockClassifier.FromReads(Sample(connected), Sample(activation)).State;
            XgDockState expected = connected is XgValueClass.Other or XgValueClass.TransportFailed
                || activation is XgValueClass.Other or XgValueClass.TransportFailed
                ? XgDockState.Unknown
                : connected == XgValueClass.One && activation == XgValueClass.One
                    ? XgDockState.Active
                    : activation == XgValueClass.One
                        ? XgDockState.Unknown
                        : XgDockState.Inactive;
            Assert.Equal(expected, state);
        }

        Assert.Equal(XgDockState.Unknown, Reads(Value(0x101), Value(0)).State);
        Assert.Equal(XgDockState.Unknown, Reads(Value(0), Value(1)).State);
        Assert.Equal(XgValueClass.Other, XgDockClassifier.Classify(Value(0x101)));
        Assert.Equal(XgValueClass.TransportFailed, XgDockClassifier.Classify(new DecodedRead
        {
            IoctlSucceeded = true,
            BytesReturned = 4,
            Buffer = null
        }));
    }

    [Fact]
    public void ExitTransition_RequiresAFreshRead()
    {
        var (guard, script, _) = NewGuard();
        guard.EnterTransition("activate");
        try
        {
            guard.RunLifecycle("GPU XGM", AsusACPI.GPUXG, "1", () => throw new InvalidOperationException("eject failed"));
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            guard.ExitTransition("activate");
        }

        Assert.False(guard.InTransition);
        Assert.Equal(0, script.Reads);
        Assert.Equal(XgMitigation.Suppressed, Try(guard, (uint)AsusACPI.PPT_APUA0, 15, explicitApply: true));
        Assert.Equal(1, script.Reads);

        script.Next.Enqueue(Inactive());
        script.Next.Enqueue(Inactive());
        Assert.Equal(XgMitigation.Suppressed, Try(guard, (uint)AsusACPI.PPT_APUA0, 15));
        Assert.Equal(1, Try(guard, (uint)AsusACPI.PPT_APUA0, 15, explicitApply: true));
        Assert.Equal(3, script.Reads);
    }

    [Fact]
    public async Task NestedGuard_DoesNotDeadlockWithTransition()
    {
        var (guard, _, _) = NewGuard(Inactive());
        var waiting = new ManualResetEventSlim(false);
        guard.TransitionWaiting = waiting;
        var nested = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transitionDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var worker = Task.Run(() =>
        {
            using var lease = guard.Enter("SetPower", "DEVS", XgOp.Power);
            Assert.True(lease.Allowed);
            var transition = Task.Run(() =>
            {
                guard.EnterTransition("activate");
                transitionDone.TrySetResult();
            });
            Assert.True(waiting.Wait(TimeSpan.FromSeconds(2)));
            int result = guard.RunGuarded("PowerLimit A0", (uint)AsusACPI.PPT_APUA0, "15", () =>
                new DevsOutcome { Result = 1, IoctlSucceeded = true, BytesReturned = 16 });
            nested.TrySetResult(result);
            Assert.False(transition.IsCompleted);
            release.Task.GetAwaiter().GetResult();
        });

        Assert.Equal(1, await nested.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(transitionDone.Task.IsCompleted);
        release.TrySetResult();
        await worker.WaitAsync(TimeSpan.FromSeconds(2));
        await transitionDone.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(guard.InTransition);
        guard.ExitTransition("activate");
    }

    [Fact]
    public async Task PendingTransition_RejectsNewAdmissionWithoutWaiting()
    {
        var (guard, script, _) = NewGuard(Inactive(), Inactive());
        var waiting = new ManualResetEventSlim(false);
        guard.TransitionWaiting = waiting;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var worker = Task.Run(() =>
        {
            using var lease = guard.Enter("SetPower", "DEVS", XgOp.Power);
            holding.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        });

        await holding.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var transition = Task.Run(() => guard.EnterTransition("activate"));
        Assert.True(waiting.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(guard.AdmissionClosed);

        int result = Try(guard, (uint)AsusACPI.PPT_TEMP9E, 90, explicitApply: true);
        Assert.Equal(XgMitigation.Suppressed, result);
        Assert.False(transition.IsCompleted);
        Assert.Equal(1, script.Reads);

        release.TrySetResult();
        await transition.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(guard.InTransition);
        guard.ExitTransition("activate");
    }

    [Fact]
    public async Task Exception_ReleasesAdmissionSoTransitionCanFinish()
    {
        var (guard, _, _) = NewGuard(Inactive());
        var waiting = new ManualResetEventSlim(false);
        guard.TransitionWaiting = waiting;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var worker = Task.Run(() =>
        {
            var thrown = Assert.Throws<InvalidOperationException>(() =>
                guard.RunGuarded("SetPower", (uint)AsusACPI.PPT_APUA0, "15", () =>
                {
                    started.TrySetResult();
                    release.Task.GetAwaiter().GetResult();
                    throw new InvalidOperationException("write failed");
                }));
            Assert.Equal("write failed", thrown.Message);
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var transition = Task.Run(() => guard.EnterTransition("activate"));
        Assert.True(waiting.Wait(TimeSpan.FromSeconds(2)));
        release.TrySetResult();
        await worker.WaitAsync(TimeSpan.FromSeconds(2));
        await transition.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(guard.InTransition);
        guard.ExitTransition("activate");
    }

    [Fact]
    public async Task CpuPowerWrites_StayOneAdmission()
    {
        var (guard, script, log) = NewGuard(Inactive());
        var waiting = new ManualResetEventSlim(false);
        guard.TransitionWaiting = waiting;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawBoth = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;

        var worker = Task.Run(() =>
        {
            Task? transition = null;
            int written = guard.ApplyCpuPowerWrites(15, 15, false, 0, true, 20, (id, value, name) =>
            {
                int n = Interlocked.Increment(ref calls);
                if (n == 1)
                {
                    transition = Task.Run(() => guard.EnterTransition("activate"));
                    Assert.True(waiting.Wait(TimeSpan.FromSeconds(2)));
                }
                int result = guard.RunGuarded(name, id, value.ToString(), () =>
                    new DevsOutcome { Result = 1, IoctlSucceeded = true, BytesReturned = 16 });
                if (n == 3)
                    sawBoth.TrySetResult(result == 1 && transition != null && !transition.IsCompleted);
                return result;
            });
            Assert.Equal(1, written);
            release.Task.GetAwaiter().GetResult();
        });

        Assert.True(await sawBoth.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, script.Reads);
        string joined = string.Join("\n", log);
        Assert.Contains("action=completed", joined);
        Assert.DoesNotContain("action=allowed", joined);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(joined, "action=completed").Count);
        release.TrySetResult();
        await worker.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(guard.InTransition);
        guard.ExitTransition("activate");
    }

    [Fact]
    public void FanFallback_DoesNotTreatSuppressedAsUnsupported()
    {
        Assert.False(XgMitigation.FanFallbackIsHardwareRejection(XgMitigation.Suppressed, 1));
        Assert.False(XgMitigation.FanFallbackIsHardwareRejection(1, XgMitigation.Suppressed));
        Assert.False(XgMitigation.FanFallbackIsHardwareRejection(1, 1));
        Assert.True(XgMitigation.FanFallbackIsHardwareRejection(0, 1));
    }

    static DecodedRead Sample(XgValueClass cls) => cls switch
    {
        XgValueClass.Zero => Value(0),
        XgValueClass.One => Value(1),
        XgValueClass.Other => Value(0x101),
        _ => TransportFailed(0)
    };
}
