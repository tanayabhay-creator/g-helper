using System.Diagnostics;

// RC71L mitigation. These ASUS IDs are ATKACPI DEVS/DSTS arguments.
// A GPUXG command value is not treated as a readback, and no status-bit layout is inferred.

public enum XgValueClass
{
    NotRead,
    Zero,
    One,
    Other,
    TransportFailed
}

public enum XgDockState
{
    Inactive,
    Active,
    Unknown,
    Transitioning
}

[Flags]
public enum XgOp
{
    None = 0,
    Power = 1,
    Fan = 2,
    Profile = 4,
    Uv = 8,
    Thermal = 16,
    Clocks = 32,
    GpuThermal = 64,
    CpuLimit = 128,
}

public readonly struct DecodedRead
{
    public bool IoctlSucceeded { get; init; }
    public uint BytesReturned { get; init; }
    public int Win32Error { get; init; }
    public byte[]? Buffer { get; init; }

    public int Raw => Buffer != null && Buffer.Length >= 4 ? BitConverter.ToInt32(Buffer, 0) : 0;
    public int Decoded => Raw - 65536;

    public override string ToString()
    {
        if (Buffer == null)
            return "class=NotRead ioctl=not_called";
        var cls = XgDockClassifier.Classify(this);
        string raw = Buffer.Length >= 4 ? Raw.ToString("X8") : "-";
        return $"class={cls} ioctl={(IoctlSucceeded ? "ok" : "fail")} bytes={BytesReturned} win32={Win32Error} raw=0x{raw} decoded={Decoded}";
    }
}

public readonly struct DevsOutcome
{
    public int Result { get; init; }
    public bool IoctlSucceeded { get; init; }
    public uint BytesReturned { get; init; }
    public int Win32Error { get; init; }
}

public readonly record struct XgDockSnapshot
{
    public XgDockState State { get; init; }
    public DecodedRead Connected { get; init; }
    public DecodedRead Activation { get; init; }
    public DateTimeOffset Utc { get; init; }
    public long MonoMs { get; init; }
    public bool HasSample { get; init; }
    public string Source { get; init; }

    public bool BlocksMutations => State is XgDockState.Active or XgDockState.Unknown or XgDockState.Transitioning;

    public static XgDockSnapshot Unsampled() => new()
    {
        State = XgDockState.Unknown,
        Source = "unsampled",
        Utc = DateTimeOffset.UtcNow,
        MonoMs = Environment.TickCount64
    };
}

public static class XgDockClassifier
{
    public static XgValueClass Classify(DecodedRead read)
    {
        if (!read.IoctlSucceeded || read.BytesReturned < 4 || read.Buffer == null || read.Buffer.Length < 4)
            return XgValueClass.TransportFailed;
        int decoded = BitConverter.ToInt32(read.Buffer, 0) - 65536;
        if (decoded == 0) return XgValueClass.Zero;
        if (decoded == 1) return XgValueClass.One;
        return XgValueClass.Other;
    }

    // Established pairs only: decoded 0 and decoded 1. Any other successful value is Unknown.
    public static XgDockSnapshot FromReads(DecodedRead connected, DecodedRead activation)
    {
        var c = Classify(connected);
        var a = Classify(activation);
        XgDockState state;
        if (c is XgValueClass.TransportFailed or XgValueClass.Other || a is XgValueClass.TransportFailed or XgValueClass.Other)
            state = XgDockState.Unknown;
        else if (c == XgValueClass.One && a == XgValueClass.One)
            state = XgDockState.Active;
        else if (a == XgValueClass.One)
            state = XgDockState.Unknown;
        else
            state = XgDockState.Inactive;

        return new XgDockSnapshot
        {
            State = state,
            Connected = connected,
            Activation = activation,
            Utc = DateTimeOffset.UtcNow,
            MonoMs = Environment.TickCount64,
            HasSample = true,
            Source = "fresh_dsts"
        };
    }
}

public sealed class XgMitigation
{
    public const int Suppressed = -2;

    public static readonly XgMitigation Shared = new();

    // Profile writers: 0x00120075 performance mode, 0x00110019 alternate profile path, 0x00090031 optional status side write.
    // GPUXG 0x00090019 is not in this set. Activation and ejection stay on RunLifecycle.
    static readonly HashSet<uint> GuardedDevices = new()
    {
        AsusACPI.PerformanceMode,
        AsusACPI.VivoBookMode,
        AsusACPI.StatusMode,
        (uint)AsusACPI.PPT_APUA0,
        (uint)AsusACPI.PPT_EDCA1,
        (uint)AsusACPI.PPT_TDCA2,
        (uint)AsusACPI.PPT_APUA3,
        (uint)AsusACPI.PPT_CPUB0,
        (uint)AsusACPI.PPT_CPUB1,
        (uint)AsusACPI.PPT_GPUCPU9C,
        (uint)AsusACPI.PPT_TEMP9E,
        (uint)AsusACPI.PPT_CROSS9F,
        (uint)AsusACPI.PPT_GPUC0,
        (uint)AsusACPI.PPT_APUC1,
        (uint)AsusACPI.PPT_GPUC2,
        AsusACPI.GPU_BASE,
        AsusACPI.GPU_POWER,
        AsusACPI.DevsCPUFan,
        AsusACPI.DevsGPUFan,
        AsusACPI.DevsCPUFanCurve,
        AsusACPI.DevsGPUFanCurve,
        AsusACPI.DevsMidFanCurve,
        AsusACPI.FanHysteresis,
    };

    public const XgOp ProfileApply = XgOp.Profile | XgOp.Fan | XgOp.Power | XgOp.Clocks | XgOp.Thermal | XgOp.GpuThermal | XgOp.CpuLimit;
    public const XgOp UvApply = XgOp.Uv | XgOp.Thermal;
    public const XgOp ResetApply = ProfileApply | XgOp.Uv;

    readonly object gate = new();
    int transition;
    int transitionPending;
    int mutations;
    int transitionWaits;
    long epoch;
    bool automaticSuspended;
    XgDockSnapshot last = XgDockSnapshot.Unsampled();

    [ThreadStatic] static XgMitigation? ticketOwner;
    [ThreadStatic] static int ticketDepth;
    [ThreadStatic] static XgDockSnapshot ticketSnapshot;
    static readonly AsyncLocal<XgOp> explicitOps = new();

    public bool Enabled { get; private set; }
    public Func<XgDockSnapshot>? ReadState { get; set; }
    public Action<string> Write { get; set; } = Logger.WriteLine;
    public Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;
    public Func<long> MonotonicMs { get; set; } = () => Environment.TickCount64;
    public ManualResetEventSlim? TransitionWaiting { get; set; }

    public static bool IsGuardedDevice(uint deviceId) => GuardedDevices.Contains(deviceId);
    public static XgOp ExplicitOperations => explicitOps.Value;

    public static XgOp OperationForDevice(uint deviceId)
    {
        if (deviceId == AsusACPI.PerformanceMode || deviceId == AsusACPI.VivoBookMode || deviceId == AsusACPI.StatusMode)
            return XgOp.Profile;
        if (deviceId == AsusACPI.DevsCPUFan || deviceId == AsusACPI.DevsGPUFan ||
            deviceId == AsusACPI.DevsCPUFanCurve || deviceId == AsusACPI.DevsGPUFanCurve ||
            deviceId == AsusACPI.DevsMidFanCurve || deviceId == AsusACPI.FanHysteresis)
            return XgOp.Fan;
        if (deviceId == (uint)AsusACPI.PPT_TEMP9E)
            return XgOp.CpuLimit;
        if (deviceId == (uint)AsusACPI.PPT_GPUC2)
            return XgOp.GpuThermal;
        return XgOp.Power;
    }

    public static bool FanFallbackIsHardwareRejection(int cpuResult, int gpuResult)
        => cpuResult != Suppressed && gpuResult != Suppressed && (cpuResult != 1 || gpuResult != 1);

    public bool InTransition
    {
        get { lock (gate) return transition > 0; }
    }

    public int TransitionWaits
    {
        get { lock (gate) return transitionWaits; }
    }

    public bool AdmissionClosed
    {
        get { lock (gate) return transition > 0 || transitionPending > 0; }
    }

    public bool AwaitingExplicit
    {
        get { lock (gate) return automaticSuspended; }
    }

    public void Enable(Func<XgDockSnapshot> read)
    {
        ReadState = read;
        Enabled = true;
    }

    public static ExplicitScope Explicit(XgOp operations)
    {
        var previous = explicitOps.Value;
        explicitOps.Value = previous | operations;
        return new ExplicitScope(previous);
    }

    public bool ControlsLocked()
    {
        if (!Enabled) return false;
        return Peek().BlocksMutations;
    }

    public XgDockSnapshot Peek()
    {
        if (!Enabled) return XgDockSnapshot.Unsampled();
        lock (gate)
        {
            if (transition > 0) return TransitionSnapshot();
        }
        var fresh = ReadFresh();
        lock (gate)
        {
            if (transition > 0) return TransitionSnapshot();
            last = fresh;
            return fresh;
        }
    }

    public Lease Enter(string caller, string api, XgOp op, uint? deviceId = null, string? requested = null)
    {
        if (!Enabled) return Lease.Bypass();
        // Nested calls belong to the admission already counted. They must not wait on a transition.
        if (ticketDepth > 0 && ticketOwner == this)
            return Lease.Nested(ticketSnapshot);

        long seen;
        lock (gate)
        {
            if (transition > 0 || transitionPending > 0)
            {
                automaticSuspended = true;
                var snap = TransitionSnapshot();
                Emit("suppressed", caller, api, deviceId, requested ?? "", "not_sent", snap, false);
                return Lease.Deny(snap);
            }
            seen = epoch;
        }

        var fresh = ReadFresh();
        lock (gate)
        {
            if (transition > 0 || transitionPending > 0 || epoch != seen)
            {
                automaticSuspended = true;
                var snap = transition > 0 || transitionPending > 0 ? TransitionSnapshot() : fresh;
                Emit("suppressed", caller, api, deviceId, requested ?? "", "not_sent concurrent_state_change", snap, false);
                return Lease.Deny(snap);
            }

            last = fresh;
            bool requestedByUser = op != XgOp.None && (explicitOps.Value & op) == op;
            if (fresh.BlocksMutations)
            {
                automaticSuspended = true;
                Emit("suppressed", caller, api, deviceId, requested ?? "", "not_sent", fresh, false);
                return Lease.Deny(fresh);
            }
            if (automaticSuspended && !requestedByUser)
            {
                Emit("suppressed", caller, api, deviceId, requested ?? "", "not_sent explicit_apply_required", fresh, false);
                return Lease.Deny(fresh);
            }

            mutations++;
            ticketOwner = this;
            ticketDepth++;
            ticketSnapshot = fresh;
            return Lease.Owned(this, fresh);
        }
    }

    public void End()
    {
        lock (gate)
        {
            if (ticketOwner != this || ticketDepth <= 0) return;
            ticketDepth--;
            if (ticketDepth != 0) return;
            ticketOwner = null;
            if (mutations > 0) mutations--;
            Monitor.PulseAll(gate);
        }
    }

    public void EnterTransition(string caller)
    {
        lock (gate)
        {
            transitionPending++;
            try
            {
                while (mutations > 0)
                {
                    transitionWaits++;
                    TransitionWaiting?.Set();
                    Monitor.Wait(gate);
                }
                transition++;
                epoch++;
                last = TransitionSnapshot();
            }
            finally
            {
                transitionPending--;
            }
        }
        if (Enabled)
            Emit("transition", caller, "transition", AsusACPI.GPUXG, "begin", "flag_set", TransitionSnapshot(), false);
    }

    public void ExitTransition(string caller)
    {
        XgDockSnapshot cleared;
        lock (gate)
        {
            if (transition > 0) transition--;
            epoch++;
            if (transition == 0) last = XgDockSnapshot.Unsampled();
            cleared = last;
            Monitor.PulseAll(gate);
        }
        if (Enabled)
            Emit("transition", caller, "transition", AsusACPI.GPUXG, "end", "flag_cleared_no_sample", cleared, true);
    }

    public int RunGuarded(string caller, uint deviceId, string requested, Func<DevsOutcome> write)
    {
        if (!Enabled || !IsGuardedDevice(deviceId))
            return write().Result;

        using var lease = Enter(caller, "DEVS", OperationForDevice(deviceId), deviceId, requested);
        if (!lease.Allowed) return Suppressed;

        DevsOutcome outcome;
        try
        {
            outcome = write();
        }
        catch (Exception ex)
        {
            Emit("failed", caller, "DEVS", deviceId, requested, "exception " + ex.GetType().Name, lease.Evidence, false);
            throw;
        }

        string action = outcome.IoctlSucceeded ? "completed" : "failed";
        Emit(action, caller, "DEVS", deviceId, requested,
            $"devs={outcome.Result} ioctl={(outcome.IoctlSucceeded ? "ok" : "fail")} bytes={outcome.BytesReturned} win32={outcome.Win32Error}",
            lease.Evidence, false);
        return outcome.Result;
    }

    public int ApplyCpuPowerWrites(int total, int slow, bool writeCpu, int cpu, bool writeFast, int fast, Func<uint, int, string, int> write)
    {
        using var lease = Enter("SetPower", "DEVS", XgOp.Power);
        if (!lease.Allowed) return Suppressed;

        int sustained = write((uint)AsusACPI.PPT_APUA3, total, "PowerLimit A3");
        int slowResult = write((uint)AsusACPI.PPT_APUA0, slow, "PowerLimit A0");
        int extra = 1;
        if (writeCpu) extra = write((uint)AsusACPI.PPT_CPUB0, cpu, "PowerLimit B0");
        else if (writeFast) extra = write((uint)AsusACPI.PPT_APUC1, fast, "PowerLimit C1");

        if (sustained == Suppressed || slowResult == Suppressed || extra == Suppressed)
            return Suppressed;
        return 1;
    }

    public int RunLifecycle(string caller, uint deviceId, string requested, Func<DevsOutcome> write)
    {
        DevsOutcome outcome;
        try
        {
            outcome = write();
        }
        catch (Exception ex)
        {
            if (Enabled)
                Emit("failed", caller, "DEVS", deviceId, requested, "exception " + ex.GetType().Name, CurrentEvidence(true), true);
            throw;
        }

        if (Enabled)
        {
            string action = outcome.IoctlSucceeded ? "completed" : "failed";
            Emit(action, caller, "DEVS", deviceId, requested,
                $"devs={outcome.Result} ioctl={(outcome.IoctlSucceeded ? "ok" : "fail")} bytes={outcome.BytesReturned} win32={outcome.Win32Error}",
                CurrentEvidence(true), true);
        }
        return outcome.Result;
    }

    public bool TryMutate(string caller, string api, string requested, XgOp op, Action mutate)
    {
        if (!Enabled)
        {
            mutate();
            return true;
        }

        using var lease = Enter(caller, api, op);
        if (!lease.Allowed) return false;
        try
        {
            mutate();
        }
        catch (Exception ex)
        {
            Emit("failed", caller, api, null, requested, "exception " + ex.GetType().Name, lease.Evidence, false);
            throw;
        }
        Emit("completed", caller, api, null, requested, "sent", lease.Evidence, false);
        return true;
    }

    public void RunCpuBoost(string caller, Action writeWindowsSetting)
    {
        try
        {
            writeWindowsSetting();
        }
        catch (Exception ex)
        {
            if (Enabled)
            {
                XgDockSnapshot evidence;
                lock (gate) evidence = last;
                Emit("failed", caller, "PowerWriteACValueIndex", null, "windows-boost", ex.GetType().Name, evidence, true);
            }
            throw;
        }

        if (Enabled)
        {
            XgDockSnapshot evidence;
            lock (gate) evidence = last;
            Emit("completed", caller, "PowerWriteACValueIndex", null, "windows-boost", "sent", evidence, true);
        }
    }

    public void LogCompleted(Lease lease, string caller, string api, string requested, string result)
    {
        if (!Enabled || !lease.Allowed || !lease.OwnsAdmission) return;
        Emit("completed", caller, api, null, requested, result, lease.Evidence, false);
    }

    public void LogFailed(Lease lease, string caller, string api, string requested, string result)
    {
        if (!Enabled || !lease.Allowed || !lease.OwnsAdmission) return;
        Emit("failed", caller, api, null, requested, result, lease.Evidence, false);
    }

    XgDockSnapshot ReadFresh()
    {
        try
        {
            if (ReadState == null) return UnknownSnapshot("reader_missing");
            return ReadState();
        }
        catch
        {
            return UnknownSnapshot("reader_exception");
        }
    }

    XgDockSnapshot CurrentEvidence(bool cached)
    {
        lock (gate)
        {
            if (transition > 0) return TransitionSnapshot();
            var snap = last;
            if (cached && snap.Source == "fresh_dsts")
                return snap with { Source = "cached_dsts" };
            return snap;
        }
    }

    XgDockSnapshot TransitionSnapshot() => new()
    {
        State = XgDockState.Transitioning,
        Utc = UtcNow(),
        MonoMs = MonotonicMs(),
        HasSample = true,
        Source = "transition_flag"
    };

    XgDockSnapshot UnknownSnapshot(string source) => new()
    {
        State = XgDockState.Unknown,
        Utc = UtcNow(),
        MonoMs = MonotonicMs(),
        HasSample = false,
        Source = source
    };

    void Emit(string action, string caller, string api, uint? device, string requested, string result, XgDockSnapshot dock, bool cached)
    {
        long now = MonotonicMs();
        long age = dock.HasSample ? now - dock.MonoMs : -1;
        string id = device.HasValue ? "0x" + device.Value.ToString("X8") : "-";
        Write(
            $"XG utc={UtcNow():O} mono_ms={now} action={action} caller={caller} api={api} device={id} value={requested} result={result} " +
            $"dock_state={dock.State} dock_source={dock.Source} dock_cached={(cached ? "yes" : "no")} dock_age_ms={age} dock_utc={dock.Utc:O} " +
            $"connected=[{dock.Connected}] activation=[{dock.Activation}]");
    }

    public readonly struct Lease : IDisposable
    {
        readonly XgMitigation? owner;
        readonly bool owned;
        public bool Allowed { get; }
        public bool Enforced { get; }
        public bool OwnsAdmission { get; }
        public XgDockSnapshot Evidence { get; }

        Lease(XgMitigation? owner, bool owned, bool allowed, bool enforced, XgDockSnapshot evidence)
        {
            this.owner = owner;
            this.owned = owned;
            Allowed = allowed;
            Enforced = enforced;
            OwnsAdmission = owned;
            Evidence = evidence;
        }

        public static Lease Bypass() => new(null, false, true, false, default);
        public static Lease Owned(XgMitigation owner, XgDockSnapshot evidence) => new(owner, true, true, true, evidence);
        public static Lease Nested(XgDockSnapshot evidence) => new(null, false, true, true, evidence);
        public static Lease Deny(XgDockSnapshot evidence) => new(null, false, false, true, evidence);

        public void Dispose()
        {
            if (owned && owner != null) owner.End();
        }
    }

    public readonly struct ExplicitScope : IDisposable
    {
        readonly XgOp previous;
        readonly bool armed;

        internal ExplicitScope(XgOp previous)
        {
            this.previous = previous;
            armed = true;
        }

        public void Dispose()
        {
            if (armed) explicitOps.Value = previous;
        }
    }
}

public enum TempOrigin
{
    FreshAsus,
    Cached,
    ThermalZone,
    Failed
}

public readonly struct TempSample
{
    public DateTimeOffset Utc { get; init; }
    public long MonoMs { get; init; }
    public TempOrigin Origin { get; init; }
    public bool TransportAttempted { get; init; }
    public bool TransportOk { get; init; }
    public uint BytesReturned { get; init; }
    public int Win32Error { get; init; }
    public string RawHex { get; init; }
    public int? Decoded { get; init; }
    public float? Reported { get; init; }
}

public sealed class XgTempTrace
{
    public static readonly XgTempTrace Shared = new();
    readonly TempSample[] ring = new TempSample[16];
    int next;
    int count;
    long lastPeriodicMs = long.MinValue;
    bool episode;
    TempOrigin lastOrigin = (TempOrigin)(-1);

    public Action<string> Write { get; set; } = Logger.WriteLine;
    public Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;
    public Func<long> MonotonicMs { get; set; } = () => Environment.TickCount64;

    public void RecordCached(float? reported)
    {
        Push(new TempSample
        {
            Utc = UtcNow(),
            MonoMs = MonotonicMs(),
            Origin = TempOrigin.Cached,
            TransportAttempted = false,
            Reported = reported,
            RawHex = "-"
        });
    }

    public void Record(DecodedRead read, TempOrigin origin, float? reported)
    {
        string raw = read.Buffer != null && read.Buffer.Length >= 4
            ? BitConverter.ToString(read.Buffer, 0, 4)
            : "-";
        var sample = new TempSample
        {
            Utc = UtcNow(),
            MonoMs = MonotonicMs(),
            Origin = origin,
            TransportAttempted = true,
            TransportOk = read.IoctlSucceeded && read.BytesReturned >= 4,
            BytesReturned = read.BytesReturned,
            Win32Error = read.Win32Error,
            RawHex = raw,
            Decoded = read.Buffer != null && read.Buffer.Length >= 4 ? read.Decoded : null,
            Reported = reported
        };
        var prior = Snapshot();
        Push(sample);
        Flush(sample, prior);
    }

    public bool TryLatest(out TempSample sample)
    {
        lock (ring)
        {
            if (count == 0)
            {
                sample = default;
                return false;
            }
            sample = ring[(next - 1 + ring.Length) % ring.Length];
            return true;
        }
    }

    public TempSample[] Snapshot()
    {
        lock (ring)
        {
            var list = new TempSample[count];
            int start = (next - count + ring.Length) % ring.Length;
            for (int i = 0; i < count; i++)
                list[i] = ring[(start + i) % ring.Length];
            return list;
        }
    }

    void Push(TempSample sample)
    {
        lock (ring)
        {
            ring[next] = sample;
            next = (next + 1) % ring.Length;
            if (count < ring.Length) count++;
        }
    }

    void Flush(TempSample sample, TempSample[] prior)
    {
        bool hot = sample.Reported >= 100;
        bool failed = sample.Origin == TempOrigin.Failed;
        bool interesting = hot || failed;
        bool sourceChanged = sample.Origin != lastOrigin;
        long now = sample.MonoMs;

        if (interesting)
        {
            if (!episode)
            {
                foreach (var old in prior)
                    Write(Format(old, "history"));
                episode = true;
            }
            Write(Format(sample, "sample"));
        }
        else if (sourceChanged || now - lastPeriodicMs >= 60000)
        {
            episode = false;
            lastPeriodicMs = now;
            Write(Format(sample, "sample"));
        }
        else
        {
            episode = false;
        }
        lastOrigin = sample.Origin;
    }

    static string Format(TempSample sample, string kind)
    {
        string transport = sample.TransportAttempted
            ? (sample.TransportOk ? "ok" : "fail")
            : "not_read";
        string decoded = sample.Decoded.HasValue ? sample.Decoded.Value.ToString() : "-";
        string reported = sample.Reported.HasValue ? sample.Reported.Value.ToString("0.##") : "-";
        return $"TEMP kind={kind} utc={sample.Utc:O} mono_ms={sample.MonoMs} origin={sample.Origin} transport={transport} bytes={sample.BytesReturned} win32={sample.Win32Error} raw={sample.RawHex} decoded={decoded} reported={reported}";
    }
}
