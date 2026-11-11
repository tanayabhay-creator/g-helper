using GHelper.Fan;
using Xunit;

public class CpuFanTraceTests
{
    static DecodedRead Sample(int decoded, bool ok = true, uint bytes = 16, int win32 = 0)
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

    static CpuFanTrace Trace(List<string> lines)
    {
        return new CpuFanTrace { Write = lines.Add };
    }

    [Fact]
    public void FailedRead_WithPlausibleBuffer_DisplaysNa()
    {
        DecodedRead read = Sample(45, ok: false, win32: 31);
        CpuFanView view = CpuFanTrace.Interpret(read);

        Assert.False(view.Valid);
        Assert.Equal("N/A", view.Text);
        Assert.False(view.ShowRpmUnit);
        Assert.Equal("45", CpuFanTrace.DecodedText(read));
    }

    [Fact]
    public void ShortRead_DisplaysNa_AndDoesNotDecodeAPartialBuffer()
    {
        DecodedRead read = Sample(45, bytes: 2);
        CpuFanView view = CpuFanTrace.Interpret(read);

        Assert.Equal("N/A", view.Text);
        Assert.False(view.ShowRpmUnit);
        Assert.Equal("-", CpuFanTrace.DecodedText(read));
        Assert.Equal(2, CpuFanTrace.RawHex(read).Split('-').Length);
    }

    [Fact]
    public void ValidZero_KeepsScaledZero()
    {
        CpuFanView view = CpuFanTrace.Interpret(Sample(0));

        Assert.True(view.Valid);
        Assert.Equal(0, view.Filtered);
        Assert.Equal(0, view.Scaled);
        Assert.Equal("0", view.Text);
        Assert.True(view.ShowRpmUnit);
    }

    [Fact]
    public void ValidRaw45_UsesExistingScale()
    {
        CpuFanView view = CpuFanTrace.Interpret(Sample(45));

        Assert.True(view.Valid);
        Assert.Equal(45, view.Filtered);
        Assert.Equal(4500, view.Scaled);
        Assert.Equal("4500", view.Text);
        Assert.True(view.ShowRpmUnit);
    }

    [Fact]
    public void RejectedDecode_DisplaysNa()
    {
        Assert.Equal("N/A", CpuFanTrace.Interpret(Sample(121)).Text);
        Assert.Equal("N/A", CpuFanTrace.Interpret(Sample(-65536)).Text);
    }

    [Fact]
    public void Log_EmitsFirstChangeAndHeartbeat_WithoutHistory()
    {
        var lines = new List<string>();
        CpuFanTrace trace = Trace(lines);
        var utc = new DateTimeOffset(2026, 10, 27, 4, 0, 0, TimeSpan.Zero);
        HeldSample missing = HeldSample.Missing;

        Assert.True(trace.Observe(Sample(45), utc, 1000, missing, missing, out string first));
        Assert.Contains("kind=first", first);
        Assert.Contains("decoded=45", first);
        Assert.Contains("displayed=4500", first);
        Assert.Contains("ioctl=ok", first);
        Assert.Contains("bytes=16", first);
        Assert.Contains("temp=unavailable source=unavailable age_ms=unavailable", first);
        Assert.Contains("power=unavailable source=unavailable age_ms=unavailable", first);

        Assert.False(trace.Observe(Sample(45), utc, 2000, missing, missing, out _));
        Assert.Single(lines);

        Assert.True(trace.Observe(Sample(0), utc, 3000, missing, missing, out string changed));
        Assert.Contains("kind=change", changed);
        Assert.Contains("displayed=0", changed);
        Assert.DoesNotContain("history", changed);

        Assert.False(trace.Observe(Sample(0), utc, 3000 + CpuFanTrace.HeartbeatMs - 1, missing, missing, out _));
        Assert.True(trace.Observe(Sample(0), utc, 3000 + CpuFanTrace.HeartbeatMs, missing, missing, out string beat));
        Assert.Contains("kind=heartbeat", beat);
        Assert.Contains("displayed=0", beat);
        Assert.Equal(3, lines.Count);
        Assert.DoesNotContain(lines, line => line.Contains("history"));
    }

    [Fact]
    public void Log_IncludesAvailableHeldSamplesAndTheirAge()
    {
        var lines = new List<string>();
        CpuFanTrace trace = Trace(lines);
        var temperature = new HeldSample { Available = true, Source = "FreshAsus", MonoMs = 500, Value = "67" };
        var power = new HeldSample { Available = true, Source = "perf_counter", MonoMs = 400, Value = "14.8" };

        trace.Observe(Sample(45, ok: false, win32: 31), DateTimeOffset.UnixEpoch, 900, temperature, power, out string line);

        Assert.Contains("kind=first", line);
        Assert.Contains("ioctl=fail", line);
        Assert.Contains("win32=31", line);
        Assert.Contains("displayed=N/A", line);
        Assert.Contains("temp=67 source=FreshAsus age_ms=400", line);
        Assert.Contains("power=14.8 source=perf_counter age_ms=500", line);
        Assert.DoesNotContain("history", line);
    }
}
