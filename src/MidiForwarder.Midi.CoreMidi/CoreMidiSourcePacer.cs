using System.Diagnostics;

namespace MidiForwarder.Midi.CoreMidi;

// CoreMIDI's virtual-source loop detector counts UMP bytes, including SysEx7 overhead.
// Keep sustained traffic below its budget; brief control-message bursts remain immediate.
internal sealed class CoreMidiSourcePacer
{
    private const double BytesPerSecond = 12_000;
    private const double BurstBytes = 8_192;
    private double _available = BurstBytes;
    private long _last = Stopwatch.GetTimestamp();

    // Called by the port's serialized output pump, once per native packet.
    public async ValueTask WaitAsync(int umpBytes, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long now = Stopwatch.GetTimestamp();
            _available = Math.Min(BurstBytes, _available + Stopwatch.GetElapsedTime(_last, now).TotalSeconds * BytesPerSecond);
            _last = now;
            if (_available >= umpBytes)
            {
                _available -= umpBytes;
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds((umpBytes - _available) / BytesPerSecond), cancellationToken).ConfigureAwait(false);
        }
    }
}
