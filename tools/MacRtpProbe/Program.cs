if (args.Contains("--help", StringComparer.Ordinal))
{
    Console.WriteLine("Run from the repository root on an Apple Silicon Mac:");
    Console.WriteLine("  dotnet run --project tools/MacRtpProbe -- --live");
    Console.WriteLine("  dotnet run --project tools/MacRtpProbe -- --fixture <hex-file>");
    Console.WriteLine("  dotnet run --project tools/MacRtpProbe -- --large-only");
    Console.WriteLine("  dotnet run --project tools/MacRtpProbe -- --installed [--start 200] [--count 30]");
    Console.WriteLine("Optional: --endpoint <CoreMIDI display name> (default: Network RTP Session 1)");
    Console.WriteLine("Connect Audio MIDI Setup's RTP session to 127.0.0.1:55006 after starting this probe.");
    return;
}

if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("This diagnostic requires macOS.");
string? Option(string name)
{
    int index = Array.IndexOf(args, name);
    if (index < 0) return null;
    if (index + 1 >= args.Length) throw new ArgumentException($"Missing value for {name}");
    return args[index + 1];
}

if (args.Contains("--installed", StringComparer.Ordinal))
{
    await RtpProbe.CheckInstalledAsync(Option("--endpoint") ?? "Network RTP Session 1",
        int.Parse(Option("--start") ?? "200", System.Globalization.CultureInfo.InvariantCulture),
        int.Parse(Option("--count") ?? "30", System.Globalization.CultureInfo.InvariantCulture));
    return;
}

await RtpProbe.RunAsync(args.Contains("--live", StringComparer.Ordinal),
    args.Contains("--large-only", StringComparer.Ordinal),
    Option("--endpoint") ?? "Network RTP Session 1", Option("--fixture"));
