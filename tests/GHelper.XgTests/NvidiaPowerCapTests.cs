using GHelper.Mode;
using Xunit;

public class NvidiaPowerCapTests
{
    [Theory]
    [InlineData(50, 50, 150, true)]
    [InlineData(50, 100, 150, false)]
    [InlineData(76, 50, 150, true)]
    [InlineData(0, 50, 150, false)]
    [InlineData(50, null, null, true)]
    public void InRange_matches_driver_bounds(int requested, int? min, int? max, bool expected)
    {
        Assert.Equal(expected, NvmlHelper.InRange(requested, min, max));
    }

    [Fact]
    public void Policy_target_for_50w_on_a_150w_default_is_one_third()
    {
        Assert.True(NvidiaPolicyTarget.TryTargetPcm(50, 150, 100, 10000, 100000, out uint pcm));
        Assert.Equal(33333u, pcm);
    }

    [Fact]
    public void Policy_target_below_the_driver_minimum_is_not_written()
    {
        Assert.False(NvidiaPolicyTarget.TryTargetPcm(50, 150, 100, 80000, 100000, out _));
        Assert.Equal(120, Math.Round(NvidiaPolicyTarget.WattsForPcm(80000, 150, 100)));
    }

    [Theory]
    [InlineData(NvidiaWattTrigger.Activation)]
    [InlineData(NvidiaWattTrigger.Startup)]
    [InlineData(NvidiaWattTrigger.ProfileChange)]
    [InlineData(NvidiaWattTrigger.SavedReplay)]
    [InlineData(NvidiaWattTrigger.ExplicitClick)]
    public void GuardedRc71L_RefusesEveryNvidiaWattTrigger(NvidiaWattTrigger trigger)
    {
        Assert.False(NvidiaWattWrite.MayAttempt(trigger, ActiveDock(), admissionClosed: false, guarded: true));
        Assert.False(NvidiaWattWrite.MayAttempt(trigger, InactiveDock(), admissionClosed: false, guarded: true));
        Assert.False(NvidiaWattWrite.MayAttempt(NvidiaWattTrigger.Activation, InactiveDock(), admissionClosed: true, guarded: true));
        Assert.False(NvidiaWattWrite.MayAttempt(NvidiaWattTrigger.SavedReplay, InactiveDock(), admissionClosed: false, guarded: true));
    }

    [Theory]
    [InlineData(NvidiaWattTrigger.Activation)]
    [InlineData(NvidiaWattTrigger.Startup)]
    [InlineData(NvidiaWattTrigger.ProfileChange)]
    [InlineData(NvidiaWattTrigger.SavedReplay)]
    [InlineData(NvidiaWattTrigger.ExplicitClick)]
    public void UnguardedModel_IsNotBlockedByTheRc71LWattGate(NvidiaWattTrigger trigger)
    {
        Assert.False(XgMitigation.Shared.Enabled);
        Assert.True(NvidiaWattWrite.MayAttempt(trigger, ActiveDock(), admissionClosed: false));
        Assert.True(NvidiaWattWrite.MayAttempt(trigger, InactiveDock(), admissionClosed: true, guarded: false));
    }

    [Fact]
    public void ActivationSliderCheckboxAndReset_DoNotCallNvidiaWattWriters()
    {
        var root = RepoRoot();
        foreach (var relative in new[]
        {
            "app/Fans.cs",
            "app/Gpu/GPUModeControl.cs",
            "app/Mode/ModeControl.cs",
            "app/Program.cs"
        })
        {
            var text = File.ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            Assert.DoesNotContain("TrySetPowerLimit", text);
            Assert.DoesNotContain("TryApplyPolicyWatts", text);
            Assert.DoesNotContain("TryReleasePowerLimit", text);
            Assert.DoesNotContain("TryRestorePolicyDefault", text);
            Assert.DoesNotContain("nvmlDeviceSetPowerManagementLimit", text);
            Assert.DoesNotContain("ClientPowerPoliciesSetStatus", text);
        }
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "app", "Fans.cs"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("app/Fans.cs");
    }

    static XgDockSnapshot ActiveDock() => new()
    {
        State = XgDockState.Active,
        HasSample = true,
        Connected = new DecodedRead { IoctlSucceeded = true, BytesReturned = 16, Buffer = new byte[16] },
        Activation = new DecodedRead { IoctlSucceeded = true, BytesReturned = 16, Buffer = new byte[16] }
    };

    static XgDockSnapshot InactiveDock() => new()
    {
        State = XgDockState.Inactive,
        HasSample = true,
        Connected = new DecodedRead { IoctlSucceeded = true, BytesReturned = 16, Buffer = new byte[16] },
        Activation = new DecodedRead { IoctlSucceeded = true, BytesReturned = 16, Buffer = new byte[16] }
    };
}
