using MightBeUseful.Midi.CoreMidi;

namespace MidiForwarder.CoreMidi.Tests;

public sealed class CoreMidiTransferLimitsTests
{
    [Fact]
    public void StandardAdapterDoesNotPassUnverifiedLargeSysExToTheNativeLibrary()
    {
        CoreMidiTransferLimits.ValidateStandardSysEx(60_000);
        var error = Assert.Throws<NotSupportedException>(() => CoreMidiTransferLimits.ValidateStandardSysEx(70_000));
        Assert.Contains("bulk-relay", error.Message, StringComparison.Ordinal);
    }
}
