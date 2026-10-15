using MightBeUseful.Midi.Windows;

namespace MidiForwarder.Core.Tests;

public sealed class WindowsMidiBackendTests
{
    [Theory]
    [InlineData(17763)]
    [InlineData(19045)]
    [InlineData(22000)]
    [InlineData(22631)]
    [InlineData(26100)]
    public void LegacyWindowsNeverProbesTheModernApi(int build)
    {
        WindowsMidiCapabilities result = WindowsMidiBackend.Select(new Version(10, 0, build),
            () => throw new InvalidOperationException("The Windows 11 API must not be loaded."));
        Assert.False(result.UseMidiServices);
        Assert.False(result.CanCreateVirtualPorts);
        Assert.Contains("requires Windows 11", result.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)]
    public void ModernWindowsUsesAvailableCapabilities(bool service, bool loopback, bool expectedService, bool expectedVirtual)
    {
        int probes = 0;
        WindowsMidiCapabilities result = WindowsMidiBackend.Select(new Version(10, 0, 26200), () =>
        {
            probes++;
            return (service, loopback);
        });
        Assert.Equal(1, probes);
        Assert.Equal(expectedService, result.UseMidiServices);
        Assert.Equal(expectedVirtual, result.CanCreateVirtualPorts);
    }

    [Fact]
    public void MissingNativeSdkFallsBackToWinMm()
    {
        WindowsMidiCapabilities result = WindowsMidiBackend.Select(new Version(10, 0, 26300),
            () => throw new DllNotFoundException("optional SDK is absent"));
        Assert.False(result.UseMidiServices);
        Assert.False(result.CanCreateVirtualPorts);
        Assert.Contains("optional SDK is absent", result.Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "To App")]
    [InlineData("From App", "")]
    [InlineData("Cable", "cable")]
    public void ClientRejectsMissingOrFeedbackCableSelections(string input, string output) =>
        Assert.Throws<InvalidOperationException>(() => WindowsMidiBackend.ValidateClientPorts(input, output));
}
