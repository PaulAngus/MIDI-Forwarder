// Test-only interoperability harness. Query vectors are pinned to the Toolkit's
// SysexProtocol and recorded FM9 evidence; this is not a production protocol API.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TestLab.Fm9;

internal interface IFm9Queries : IAsyncDisposable
{
    Task<string> Identify(CancellationToken token);
    Task<string> State(CancellationToken token);
    int Sent { get; }
}

internal sealed record Fm9Selection(string EndpointName, string LegacyInputName, string LegacyOutputName)
{
    internal static Fm9Selection Load(string path)
    {
        var value = JsonSerializer.Deserialize<Fm9Selection>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Missing explicit FM9 selection.");
        if (value != new Fm9Selection("FM9", "FM9 MIDI In", "FM9 MIDI Out"))
        {
            throw new InvalidDataException("Only the explicitly selected existing FM9 is allowed. No fallback.");
        }
        return value;
    }
}

internal static class Fm9ReadOnly
{
    // F0 00 01 74: Fractal, model 12: FM9. 0C 7F reads, never selects.
    private static readonly string[] Hex = ["F00001747F007AF7", "F000017412081FF7", "F0000174120D7F7F1AF7", "F0000174120C7F64F7"];
    internal static byte[] Query(int index) => Convert.FromHexString(Hex[index]);
    internal static bool Allowed(byte[] bytes) => Hex.Contains(Convert.ToHexString(bytes), StringComparer.Ordinal);
    internal static void Authorize(byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Allowed(bytes)) { throw new InvalidOperationException("Outside the four exact read-only FM9 queries."); }
    }
    internal static bool Reply(byte[] bytes, byte function, int? length = null)
    {
        if (bytes.Length < 8 || length.HasValue && bytes.Length != length.Value ||
            !bytes.AsSpan(0, 6).SequenceEqual(new byte[] { 0xf0, 0, 1, 0x74, 0x12, function }) || bytes[^1] != 0xf7)
        {
            return false;
        }
        byte checksum = 0;
        for (int i = 0; i < bytes.Length - 2; i++) { checksum ^= bytes[i]; }
        return (checksum & 127) == bytes[^2] && bytes.AsSpan(1, bytes.Length - 2).IndexOfAnyInRange((byte)128, byte.MaxValue) < 0;
    }
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static void CheckPolicy()
    {
        for (int i = 0; i < Hex.Length; i++)
        {
            byte[] original = Query(i);
            Authorize(original, CancellationToken.None);
            for (int j = 0; j < original.Length; j++)
            {
                byte[] changed = original.ToArray(); changed[j] ^= 1;
                if (Allowed(changed)) { throw new InvalidDataException("Query allowlist is not exact."); }
            }
        }
        foreach (byte[] command in new byte[][] { [0xc0, 0], [0xb0, 34, 0], [0x90, 60, 100], [0xf0, 0, 1, 0x74, 0x12, 0x0c, 0, 0x1b, 0xf7] })
        {
            if (Allowed(command)) { throw new InvalidDataException("Mutation passed query allowlist."); }
        }
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { Authorize(Query(0), cancel.Token); throw new InvalidDataException("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        byte[] identity = Convert.FromHexString("F00001741264000073F7");
        if (!Reply(identity, 0x64, 10)) { throw new InvalidDataException("Captured FM9 identity rejected."); }
        for (int i = 0; i < identity.Length; i++)
        {
            byte[] corrupt = identity.ToArray(); corrupt[i] ^= 1;
            if (Reply(corrupt, 0x64, 10)) { throw new InvalidDataException("Corrupt reply accepted."); }
        }
    }
    internal static async Task<int> Run(string[] args, Func<Fm9Selection, IFm9Queries> open, string scope)
    {
        if (args.Length != 3 || args[0] is not ("--fm9" or "--check-policy"))
        {
            throw new ArgumentException("Use --fm9|--check-policy SELECTION_JSON RESULT_JSON.");
        }
        var tests = new List<object>();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
        using var progress = new StreamWriter(new FileStream(args[2] + ".progress.jsonl", FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        void Progress(string phase, string name, int? total = null) => progress.WriteLine(JsonSerializer.Serialize(new { phase, name, total, utc = DateTimeOffset.UtcNow }));
        void Passed(string name) { tests.Add(new { name, status = "passed" }); Progress("passed", name); }
        Progress("discovered", "", args[0] == "--fm9" ? 7 : 2);
        var connections = new List<IFm9Queries>();
        string? firmware = null, before = null, after = null;
        int sent = 0;
        bool passed = false;
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            CheckPolicy();
            Passed("Exact read-query allowlist rejects mutations and pre-cancelled sends");
            var selection = Fm9Selection.Load(args[1]);
            Passed("Explicit existing FM9 selection; no fallback or port creation");
            if (args[0] == "--fm9")
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                Progress("started", "Open existing FM9 and read identity/firmware");
                await using (var connection = open(selection))
                {
                    connections.Add(connection);
                    firmware = await connection.Identify(deadline.Token);
                    Passed("FM9 identity and firmware query/reply");
                    Progress("started", "Repeated current-preset and bracketed current-scene reads");
                    before = await connection.State(deadline.Token);
                    if (before != await connection.State(deadline.Token)) { throw new IOException("Current state changed during repeated reads."); }
                    Passed("Repeated current-preset and bracketed current-scene reads agree");
                    int count = connection.Sent;
                    using var cancel = new CancellationTokenSource(); cancel.Cancel();
                    try { await connection.State(cancel.Token); throw new IOException("Cancelled read succeeded."); }
                    catch (OperationCanceledException) { }
                    if (count != connection.Sent) { throw new IOException("Pre-cancelled read sent data."); }
                    Passed("Pre-cancelled hardware read sends zero bytes");
                    sent += connection.Sent;
                }
                // New ownership generation after disposal, with no automatic replay.
                Progress("started", "Dispose and reopen the same existing FM9 transport");
                await Task.Delay(1000, deadline.Token);
                await using (var connection = open(selection))
                {
                    connections.Add(connection);
                    if (firmware != await connection.Identify(deadline.Token)) { throw new IOException("Device firmware changed after reopen."); }
                    after = await connection.State(deadline.Token);
                    sent += connection.Sent;
                }
                Passed("Dispose and reopen the same existing FM9 transport");
                if (before != after) { throw new IOException("Current preset or scene changed across the run."); }
                Passed("Observed preset and scene preserved across reads and reopen");
            }
            passed = true;
        }
        catch (Exception error)
        {
            tests.Add(new { name = "Read-only FM9 coverage", status = "failed", error = error.ToString() });
            Progress("failed", "Read-only FM9 coverage");
        }
        sent = connections.Sum(connection => connection.Sent);
        var result = new
        {
            schemaVersion = 1, suite = "native-fm9-read-only", status = passed ? "passed" : "failed",
            total = tests.Count, failed = passed ? 0 : 1, skipped = 0, seconds = started.Elapsed.TotalSeconds, tests,
            scope, model = firmware == null ? null : "FM9", firmware, beforeStateSha256 = before, afterStateSha256 = after,
            sentQueries = sent, allowedRequestsHex = Hex, virtualPortsCreated = false,
            limits = "Current-state observations only; no preset/scene changes, writes, dumps, network routing or desktop qualification. Brief external changes between observations cannot be ruled out."
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
        File.WriteAllText(args[2], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"FM9 read-only: {result.total} checks; {result.failed} failed; {sent} read queries.");
        return passed ? 0 : 1;
    }
}
