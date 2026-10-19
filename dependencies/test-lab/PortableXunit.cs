using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Xunit.Runners;

// Versioned source distributed with the Test Lab adapter build helpers.
internal static class PortableXunit
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static int Run(string[] args, string[] allowedAssemblies, string? physicalReadOnlyTest = null, TimeSpan? executionTimeout = null)
    {
        if (args.Length is not (2 or 3) || !allowedAssemblies.Contains(args[0], StringComparer.Ordinal) || args.Length == 3 && args[2] != "Native")
        { throw new ArgumentException("Select a packaged test assembly and output JSON path."); }
        TimeSpan budget = executionTimeout ?? TimeSpan.FromMinutes(10);
        if (budget < TimeSpan.FromSeconds(1) || budget > TimeSpan.FromHours(1)) { throw new ArgumentOutOfRangeException(nameof(executionTimeout)); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
        using var progress = new StreamWriter(new FileStream(args[1] + ".progress.jsonl", FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        object progressLock = new();
        void Progress(string phase, string name, int? total = null)
        {
            lock (progressLock) { progress.WriteLine(JsonSerializer.Serialize(new { phase, name, total, utc = DateTimeOffset.UtcNow })); }
        }
        var rows = new ConcurrentQueue<object>();
        var errors = new ConcurrentQueue<object>();
        var excluded = new ConcurrentQueue<string>();
        bool native = args.Length == 3 || physicalReadOnlyTest is not null;
        using var complete = new ManualResetEventSlim();
        using var runner = AssemblyRunner.WithoutAppDomain(Assembly.Load(args[0]).Location);
        ExecutionCompleteInfo? summary = null;
        // Hardware is excluded unless an application explicitly selects one named read-only case.
        runner.TestCaseFilter = test =>
        {
            bool physical = test.Traits.Any(t => t.Key.Equals("Category", StringComparison.OrdinalIgnoreCase)
                && t.Value.Any(v => v.Equals("Hardware", StringComparison.OrdinalIgnoreCase) || v.Equals("Compatibility", StringComparison.OrdinalIgnoreCase)));
            bool nativeCase = test.TestMethod.TestClass.Class.Name.EndsWith(".CoreMidiDuplexPortTests", StringComparison.Ordinal)
                || test.TestMethod.Method.Name == "NativeCoreMidiLibraryLoadsAndEnumeratesWithoutHardware";
            bool applicable = OperatingSystem.IsWindows() || test.SkipReason != "Tests Windows mandatory file-sharing semantics.";
            bool include = physicalReadOnlyTest is null ? !physical && applicable && nativeCase == native
                : physical && test.TestMethod.TestClass.Class.Name + "." + test.TestMethod.Method.Name == physicalReadOnlyTest;
            if (!include) { excluded.Enqueue(test.DisplayName); }
            return include;
        };
        runner.OnDiscoveryComplete = result => Progress("discovered", "", result.TestCasesToRun);
        runner.OnTestStarting = test => Progress("started", test.TestDisplayName);
        runner.OnTestPassed = test => { rows.Enqueue(new { name = test.TestDisplayName, status = "passed", seconds = test.ExecutionTime }); Progress("passed", test.TestDisplayName); };
        runner.OnTestSkipped = test => { rows.Enqueue(new { name = test.TestDisplayName, status = "skipped", reason = test.SkipReason }); Progress("skipped", test.TestDisplayName); };
        runner.OnTestFailed = test => { rows.Enqueue(new { name = test.TestDisplayName, status = "failed", error = test.ExceptionMessage, stack = test.ExceptionStackTrace }); Progress("failed", test.TestDisplayName); };
        runner.OnErrorMessage = error => errors.Enqueue(new { error.ExceptionMessage, error.ExceptionStackTrace });
        runner.OnExecutionComplete = result => { summary = result; complete.Set(); };
        runner.Start(new AssemblyRunnerStartOptions { Parallel = false, PreEnumerateTheories = true });
        if (!complete.Wait(budget))
        {
            runner.Cancel();
            errors.Enqueue(new { ExceptionMessage = "Test execution exceeded its declared bound." });
            var partial = new
            {
                schemaVersion = 1,
                suite = native ? "Native" : "Functional",
                status = "failed",
                total = rows.Count,
                failed = 0,
                skipped = 0,
                tests = rows.ToArray(),
                excluded = excluded.ToArray(),
                errors = errors.ToArray(),
                maximumExecutionSeconds = budget.TotalSeconds
            };
            File.WriteAllText(args[1], JsonSerializer.Serialize(partial, JsonOptions));
            Console.Error.WriteLine("Test execution timed out; partial results and per-case progress retained."); Environment.Exit(1);
        }
        if (!SpinWait.SpinUntil(() => runner.Status == AssemblyRunnerStatus.Idle, TimeSpan.FromSeconds(5))) { Environment.Exit(1); }
        string status = summary is null || summary.TotalTests == 0 || summary.TestsFailed > 0 || !errors.IsEmpty ? "failed"
            : summary.TestsSkipped > 0 ? "incomplete" : "passed";
        var report = new
        {
            schemaVersion = 1,
            suite = native ? "Native" : "Functional",
            status,
            total = summary?.TotalTests ?? 0,
            failed = summary?.TestsFailed ?? 0,
            skipped = summary?.TestsSkipped ?? 0,
            seconds = summary?.ExecutionTime ?? 0,
            maximumExecutionSeconds = budget.TotalSeconds,
            tests = rows.ToArray(),
            excluded = excluded.ToArray(),
            errors = errors.ToArray()
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
        File.WriteAllText(args[1], JsonSerializer.Serialize(report, JsonOptions));
        Console.WriteLine($"{args[0]}: {report.total} tests, {report.failed} failed, {report.skipped} skipped.");
        return status == "passed" ? 0 : status == "incomplete" ? 2 : 1;
    }
}
