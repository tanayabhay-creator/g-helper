using GHelper.Gpu;
using Xunit;

public class GpuFanPresentationTests
{
    [Fact]
    public void XgMobile_DoesNotShowInternalEndpoint()
    {
        GpuFanPresentation shown = GpuFanPresentation.ForDisplayedGpu(displayedGpuIsXgMobile: true, internalEndpointReading: 4500);

        Assert.Equal("N/A", shown.Text);
        Assert.False(shown.ShowRpmUnit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-100)]
    [InlineData(0)]
    [InlineData(4500)]
    public void XgMobile_LabelIgnoresInternalReading(int? reading)
    {
        GpuFanPresentation shown = GpuFanPresentation.ForDisplayedGpu(true, reading);

        Assert.Equal("N/A", shown.Text);
        Assert.False(shown.ShowRpmUnit);
        Assert.Equal("N/A", GpuFanPresentation.ForLabeledSpeed(true, "4500RPM"));
    }

    [Fact]
    public void InternalGpu_KeepsItsOwnReading()
    {
        GpuFanPresentation shown = GpuFanPresentation.ForDisplayedGpu(false, 4500);

        Assert.Equal("4500", shown.Text);
        Assert.True(shown.ShowRpmUnit);
        Assert.Equal("4500RPM", GpuFanPresentation.ForLabeledSpeed(false, "4500RPM"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    public void MissingInternalReading_StaysBlank(int? reading)
    {
        GpuFanPresentation shown = GpuFanPresentation.ForDisplayedGpu(false, reading);

        Assert.Equal("", shown.Text);
        Assert.False(shown.ShowRpmUnit);
        Assert.Null(GpuFanPresentation.ForLabeledSpeed(false, null));
    }

    [Fact]
    public void ZeroReading_RemainsVisibleOnInternalGpu()
    {
        GpuFanPresentation shown = GpuFanPresentation.ForDisplayedGpu(false, 0);

        Assert.Equal("0", shown.Text);
        Assert.True(shown.ShowRpmUnit);
    }

    [Fact]
    public void UnsupportedInternalIdentity_IsNotRelocated()
    {
        GpuFanPresentation hidden = GpuFanPresentation.InternalFan(identitySupported: false, reading: 4500);

        Assert.Equal("", hidden.Text);
        Assert.False(hidden.ShowRpmUnit);
    }

    [Fact]
    public void SupportedCpuIdentity_KeepsItsReading()
    {
        GpuFanPresentation cpu = GpuFanPresentation.InternalFan(identitySupported: true, reading: 4500);

        Assert.Equal("4500", cpu.Text);
        Assert.True(cpu.ShowRpmUnit);
    }
}
