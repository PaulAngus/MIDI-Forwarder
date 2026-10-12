namespace MidiForwarder.Core;

/// <summary>One desktop instance per role; diagnostic commands do not acquire this lease.</summary>
public static class ProcessInstance
{
    public static Mutex? TryAcquire(string name)
    {
        // Keep the named object alive, without thread-affine mutex ownership.
        var lease = new Mutex(false, name, out bool created);
        if (created) { return lease; }
        lease.Dispose();
        return null;
    }
}
